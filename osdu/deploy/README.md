# Deploying OSDU Delivery

Three images, built from the OSDU Delivery hosts in `osdu/hosts`:

| Image | Built from | Scales on | Behind the ingress? |
| --- | --- | --- | --- |
| `osdu-delivery-control-plane` | `osdu/deploy/docker/control-plane.Dockerfile` | one replica (dispatch has one owner) | yes, under `/api` |
| `osdu-delivery-gui` | `osdu/deploy/docker/gui.Dockerfile` | trivially (static) | yes, under `/` |
| `osdu-delivery-worker` | `osdu/deploy/docker/worker.Dockerfile` | the control plane's replica target (KEDA), or started by the control plane itself (managed workers, on Radix) | never (pull model: it polls the control plane for work, no inbound surface) |

**All three build from the repository root.** The .NET images publish hosts that reference both `osdu/src` and
`sqlflow/src`, and the GUI image compiles `osdu/gui` together with the vendored `sqlflow/gui` sources it imports,
so no image can be built from a narrower context. The root `.dockerignore` keeps the context small.

```bash
docker build -f osdu/deploy/docker/control-plane.Dockerfile -t osdu-delivery-control-plane:latest .
docker build -f osdu/deploy/docker/worker.Dockerfile       -t osdu-delivery-worker:latest .
docker build -f osdu/deploy/docker/gui.Dockerfile          -t osdu-delivery-gui:latest .
```

The image names carry the product rather than the platform, because the vendored SQLFlow in `sqlflow/` ships its
own `sqlflow-*` images from its own deployment assets; these are different artifacts. Project, namespace, binary
and environment-variable names stay `SqlFlow.*` and `SQLFLOW_*`, as the project's naming rule says.

A fourth image is optional: the MCP server, for AI assistants and for the control plane's own chat assistant
([../docs/reference/guides/mcp.md](../docs/reference/guides/mcp.md)).

| Image | Built from | Scales on | Behind the ingress? |
| --- | --- | --- | --- |
| `osdu-delivery-mcp` | `osdu/deploy/docker/mcp.Dockerfile` | trivially (stateless, holds no credential) | yes, under `/mcp`, when the estate offers an assistant |

```bash
docker build -f osdu/deploy/docker/mcp.Dockerfile -t osdu-delivery-mcp:latest .
```

It is a Rust binary, built from `osdu/hosts/osdu-delivery-mcp` and SQLFlow's server under `sqlflow/tools`, with the
documentation compiled into it; the root `.dockerignore` re-includes the documentation trees it reads. It is in the
compose stack (behind a profile), in `k8s/mcp.yaml`, in the Radix application, and, when given its image, in the
Bicep templates and the Terraform configuration (with an AI Foundry account for the chat assistant).
`deploy.ps1` (through `deploy-dev.ps1`, `deploy-test.ps1` and `deploy-prod.ps1`) rolls it out with the other tiers
when the estate has it.

## How the tiers fit together

- **The control plane** is the API, the scheduler, the managed git sync and the run dispatcher. It owns the run
  queue through a lease in the catalog, so it runs **one replica**: a second adds no dispatch capacity and refuses
  every node call that lands on it.
- **Nodes** poll the control plane for work over the node protocol, authenticated with a personal access token
  minted with the `node` scope. A node opens **no catalog connection**: the run's definition, its YAML, its
  lineage context and its live trace all travel over that protocol.
- **The databases of an estate.** Two: all of the metadata is one database, and the data the flows work in, which is
  where the volume is, is the other. The flows name both in three-part names (`OsduData.arc.*`,
  `OsduDelivery.osdu.v_dim_*`), so every layout keeps these names:

  | Database | Holds | Reached as | Grows with |
  | --- | --- | --- | --- |
  | `OsduDelivery` | the metadata: SQLFlow's catalog schema (pipelines, runs, schedules, lineage, sources, users) and the delivery module's `osdu` schema beside it (the record ledger, mappings, templates, OSDU caches, dimension tables and views) | `SQLFLOW_CATALOG_DB`: the control plane's catalog connection, and on a node the same variable for the ledger and the flows that read the dimension views | the estate's activity, and every record ever delivered |
  | `OsduData` | the data: what pre-ingestion lands from the source systems, and the keyed ingestion tables the OSDU flows read, each in a schema of its own | `${env:OSDU_DATA_DB}`, on the control plane and on every node | the source data |

  The two schemas of the metadata database stay separate things: their own EF contexts, their own migration
  histories, their own versions, and no foreign key between them. They share the database and its one connection,
  `SQLFLOW_CATALOG_DB`: the module has no connection of its own, and a host still given the retired
  `SQLFLOW_OSDU_DB` or `Osdu:Database:Connection` ignores it.

  The metadata database needs no setting of its own. The **source** database a flow reads is allowed snapshot
  isolation once, so a record and its child rows are read as one instant (Azure SQL Database allows it by
  default): `ALTER DATABASE [OsduData] SET ALLOW_SNAPSHOT_ISOLATION ON;`
- **The OSDU ledger on a node.** The delivery engine reads and writes the `osdu` schema per record while it plans
  and delivers. SQLFlow gives a node no catalog connection, so every node is given the catalog database's in
  `SQLFLOW_CATALOG_DB`, with a login on the `osdu` schema alone: a node without it refuses to start and takes no work.
  The flows that build and read the dimension tables and views name the same variable. The values a run's flows
  resolve beside it (`OSDU_DATA_DB`, the landing storage) reach the run one of two ways: on Azure the control plane's configuration sets them for every run
  (`Osdu:Properties:<NAME>`, below) and the run carries them to its node, so a node holds nothing else; in the compose
  and Kubernetes layouts they are the node's own environment variables. See
  [../docs/reference/concepts/environment-variables.md](../docs/reference/concepts/environment-variables.md).
- **The delivery chain** is three flows in lineage order: a SQLFlow pre-ingestion flow lands the source files, a
  SQLFlow ingestion flow loads the keyed ingestion tables, and the OSDU flow reads those tables and delivers. The
  data database is wired under the fixed name `${env:OSDU_DATA_DB}`, so a flow document moves from test to prod
  unchanged.
- **The OSDU partitions are configuration**
  ([Configured partitions](../docs/reference/concepts/partitions.md#configured-partitions)). The control plane
  reads `Osdu:Partitions:<data-partition-id>` (as environment variables `Osdu__Partitions__<id>__Default`,
  `__Description` and `__Properties__<NAME>`), registers each partition when it starts, and gives the runs bound
  to it those values for their flows' `${env:NAME}` references: `OSDU_URL`,
  `OSDU_TOKEN_URL`, `OSDU_SCOPE`, `OSDU_CLIENT_ID`, `OSDU_CLIENT_SECRET`, the ACLs, the legal tag, and any other name
  a flow spells. It is not ready without at least one partition, and with several exactly one is `Default`. A value
  whose name ends in `SECRET`, `PASSWORD` or `TOKEN` (or names an API key) must be a reference,
  `${keyvault:<vault>/<secret>}` or `${env:NAME}`, never the secret itself: the control plane refuses to start
  otherwise. Only the control plane is given these settings; the runs carry the values to the nodes, which resolve a
  reference with their own identity or environment, so a node needs no OSDU variable of its own.

## Local / single host: docker compose

Nodes need a node-scoped token, which can only be minted once the control plane is running, so a first start is
two steps:

```bash
cd osdu/deploy/compose
cp .env.example .env    # set the secrets and the OSDU partition's values
docker compose up -d --build mssql dbinit controlplane gui
# sign in as the admin, mint the token (POST /api/v1/me/tokens with scopes ["node"], or the GUI's token page),
# put it in .env as SQLFLOW_NODE_TOKEN, then:
docker compose up -d --build worker
docker compose up -d --scale worker=3   # more compute, nothing else changes
```

The MCP server is behind a profile, since not every estate offers an assistant:

```bash
docker compose --profile mcp up -d --build mcp     # http://localhost:8787/mcp
```

GUI at <http://localhost:8081>, API at <http://localhost:5000>. Bootstrap provisioning creates the `OsduDelivery`
metadata database (the compose file turns on `ControlPlane__Bootstrap__AllowCreate`; the default only migrates a
database that already exists), applies SQLFlow's catalog migrations and then the OSDU module's beside them, seeds
roles, and creates the admin from `.env` on first start. The `dbinit` service creates the `OsduData` data database,
which nothing else creates: SQLFlow makes schemas and tables inside a database, never a database.

The stack configures one partition, `dev` (the one the sample estate's flows name), from `.env`: `OSDU_URL`,
`OSDU_TOKEN_URL`, `OSDU_SCOPE`, `OSDU_CLIENT_ID`, `OSDU_ACL_OWNER`, `OSDU_ACL_VIEWER` and `OSDU_LEGAL_TAG` are
required (`.env.example` holds example values that let the stack start and plan). The partition's client secret is the
reference `${env:OSDU_CLIENT_SECRET}`, which the control plane and the worker resolve from `OSDU_CLIENT_SECRET` in
`.env`. To deliver to another partition, rename `dev` in `docker-compose.yml` and in the flows that name it.

## Azure: one configuration, set up by the deployment

On Azure, an estate is ready to use when its deployment ends: the Bicep templates and the Terraform configuration
(next sections) build the same estate the same way, in three layers.

1. **The configuration is the only description of the estate**: the SQL databases the flows read and write, the OSDU
   partitions, the landing storage, the flow repositories, the further values every run resolves, who owns the
   databases, and the node credential. It names secrets, never holds them: a secret is a `${keyvault:<vault>/<secret>}`
   reference.
2. **The deployment creates the resources and the identities** (one per app, and a setup identity no app runs as),
   grants each what it needs, and gives the control plane its configuration; a worker gets its identity, its node
   credential and the ledger's connection, nothing else. Inside the deployment, the setup identity runs
   `setup/Setup-Estate.ps1` twice: it generates the secrets nobody provides (the JWT signing key, the initial admin's
   password, the node credential) into the vault when they are missing, keeping any it finds, and, as the Entra
   administrator of the SQL server created there, it creates the apps' database users with their rights and makes
   the people the configuration names owners of every database. Both steps run at every deployment and can run again.
3. **The control plane applies its configuration at every start**, in one ordered pass of named steps: SQLFlow's
   catalog and the OSDU module migrated, the initial admin, the node credential, the repositories (registered and
   synced), the partitions and the values every run is given. `GET /api/v1/bootstrap` shows each step, and
   `/health/startup` answers when they are all done. A step that fails is retried, so a secret a person puts in the
   vault later is picked up without a redeploy; the control plane keeps serving meanwhile.

What a person does is put in the vault the secrets only they have, under the names the configuration gives: each
OSDU partition's client secret (`osdu-<data-partition-id>-client-secret` unless the partition names another), and for
the private flow repositories either the private key of the estate's GitHub App or each repository's token (a GitHub
fine-grained token with read access to the repository's contents). Until then the partition's step, or the
repository's sync, reports the secret it cannot read.

**Private GitHub repositories are best read with a GitHub App.** It belongs to the organization rather than a person,
needs only Contents: Read-only on the repositories it is installed on, and the control plane and each worker ask it for
a short-lived token for every sync and checkout, so no token is stored. Create it and install it on the flow repository
([Reading a private GitHub repository with a GitHub App](../../sqlflow/docs/reference/guides/github-app.md)), put its `.pem` in the vault, and name it in the
configuration (`githubApp` in the Bicep, `github_app` in the Terraform): every GitHub repository that names no token of
its own is then read with it, and a repository the App should not read sets `useGitHubApp` (`use_github_app`) to false.

Once it is set up, the estate:

- reads its source files from the landing storage account (`LANDING_URL`, `abfss://<container>@<account>.dfs.core.windows.net`),
  which the worker's identity may read and write and the control plane's read; a flow names it in its source location
  (`${env:LANDING_URL}/<folder>`);
- reads and writes each SQL connection (`OSDU_DATA_DB` by default) as its own managed identity;
- has migrated its catalog and the `osdu` schema, and has its initial admin;
- answers in the GUI's chat assistant, when the MCP server and a Foundry model are deployed;
- syncs its flow repositories.

`deploy.ps1 -Infrastructure` (below) waits for the start-up pass after an apply and shows each step, each repository's
sync and whether the chat assistant is on.

## Azure Container Apps: `bicep/`

The same three-tier layout on managed infrastructure, one template per tier plus a composition. The `.json` files
beside each `.bicep` are the compiled ARM templates; rebuild them with `az bicep build --file <name>.bicep` after
editing a template.

| Template | Deploys |
| --- | --- |
| `main.bicep` | The full estate: Log Analytics and the Container Apps environment, a Key Vault holding every secret, the setup identity and its two deployment scripts, the Azure SQL metadata database (`OsduDelivery`) and each data database `sqlConnections` names, the landing storage account, the three apps below with the control plane's configuration, and optionally the MCP server and an AI Foundry account. |
| `control-plane.bicep` | The API as an always-on Container App. `main.bicep` runs it API-only and gives it its configuration as `settings` (`Osdu__Partitions__<id>__...`, `Osdu__Properties__<NAME>`, `ControlPlane__Bootstrap__...`); standalone it also hosts the in-process worker. |
| `worker.bicep` | One worker pool: no ingress, scaled 0..N on the control plane's replica target by the built-in KEDA metrics-api scaler, authenticated with the node credential. One deployment per pool. |
| `gui.bicep` | The SPA behind its own ingress. |
| `mcp.bicep` | The MCP server behind its own ingress (public: the assistant's model calls it from AI Foundry), one replica, answering only on its own host name. When `mcpImage` is set. |
| `ai-foundry.bicep` | An AI Foundry account and project with a model deployment, the app identities granted keyless access. When `aiFoundryName` is set. |
| `entra-app.bicep` | The Entra app registration users sign in with (app roles, assignment required), when `provisionEntraApp` is on. |

`mcp.bicep` and `ai-foundry.bicep` are SQLFlow's own (`sqlflow/deploy/bicep`), with the MCP server's host allowlist
added, kept here so the estate deploys from this directory alone.

```bash
az group create -n osdu-delivery -l <region>
az acr create -g osdu-delivery -n <registry> --sku Basic
az acr build -r <registry> -t osdu-delivery-control-plane:latest -f osdu/deploy/docker/control-plane.Dockerfile .
az acr build -r <registry> -t osdu-delivery-worker:latest        -f osdu/deploy/docker/worker.Dockerfile .
az acr build -r <registry> -t osdu-delivery-gui:latest           -f osdu/deploy/docker/gui.Dockerfile .
az acr build -r <registry> -t osdu-delivery-mcp:latest           -f osdu/deploy/docker/mcp.Dockerfile .

cat > partitions.json <<'EOF'
[
  {
    "id": "dev",
    "default": true,
    "description": "<what the partition is for>",
    "url": "https://<platform host>",
    "tokenUrl": "https://login.microsoftonline.com/<tenant id>/oauth2/v2.0/token",
    "scope": "<OSDU resource>/.default",
    "clientId": "<client id>",
    "aclOwner": "data.default.owners@<partition>.<domain>",
    "aclViewer": "data.default.viewers@<partition>.<domain>",
    "legalTag": "<partition>-<legal tag>"
  }
]
EOF

az deployment group create -g osdu-delivery -f osdu/deploy/bicep/main.bicep \
  -p acrName=<registry> \
     controlPlaneImage=<registry>.azurecr.io/osdu-delivery-control-plane:latest \
     workerImage=<registry>.azurecr.io/osdu-delivery-worker:latest \
     guiImage=<registry>.azurecr.io/osdu-delivery-gui:latest \
     mcpImage=<registry>.azurecr.io/osdu-delivery-mcp:latest \
     aiFoundryName=<globally unique name> aiFoundryModelName=<model, e.g. gpt-5.1> \
     osduPartitions=@partitions.json \
     sqlAdmins='[{"name": "<group display name>", "objectId": "<group object id>", "type": "group"}]' \
     sqlConnections='{"OSDU_DATA_DB": {"database": "OsduData"}}' \
     landingStorage='{}' \
     repositories='{"flows": {"remoteUrl": "https://github.com/<owner>/<repository>.git"}}' \
     githubApp='{"appId": "<the App's client ID>", "privateKeySecretName": "github-app-private-key"}'

# The secrets only you have, under the names the deployment gives (outputs osduPartitions, githubAppPrivateKeySecret
# and keyVaultName):
az keyvault secret set --vault-name <vault> --name osdu-dev-client-secret --file <file holding the client secret>
az keyvault secret set --vault-name <vault> --name github-app-private-key --file <the App's .pem>
# Or, without an App, the repository's own token ("credentialSecretName": "github-flows-token" in its entry):
# az keyvault secret set --vault-name <vault> --name github-flows-token --file <file holding the token>
```

Leave `mcpImage` and `aiFoundryName` out for an estate without the chat assistant. Every deployment of `main.bicep`
sets each app's image from its parameters (ARM has no way to leave the image an app serves), so a redeploy names the
images to run: the ones `deploy.ps1` rolled forward, or `imageTag` with `acrName`. Sign in at the `guiUrl` output as
`admin`, with the password the deployment generated (`az keyvault secret show --vault-name <vault> --name
osdu-delivery-admin-password --query value -o tsv`). Point CLI remotes at `controlPlaneBaseUrl`.

`osduPartitions` takes at least one partition; with several, exactly one has `"default": true`. Further values a flow
spells go under a partition's `"properties"` (for example `OSDU_LOGTYPE_ACL_OWNER`). A partition's client secret is
never a parameter: the control plane's `OSDU_CLIENT_SECRET` is the text `${keyvault:<vault>/<secret>}`, the secret
`osdu-<id>-client-secret` (or the one `"clientSecretName"` names), which the node that runs a run resolves with its
own identity. How the Kubernetes layout maps onto Container Apps:

- **Two origins instead of a path split**: every Container App has its own ingress FQDN, so `main.bicep` wires the
  GUI's `SQLFLOW_API_BASE_URL` to the control plane URL and CORS-lists the GUI origin on the control plane. Put
  Front Door or Application Gateway in front of both apps to restore the one-host layout, then blank both.
- **KEDA is built in**: `worker.bicep` reads the same scale-target endpoint as `worker-pool.yaml`, with
  `minReplicas: 0` and the node credential as the scaler's credential. Add a pool with another deployment of it
  (`-p name=osdu-delivery-worker-<pool> pool=<pool>`).
- **Secrets live in Key Vault, read by managed identity**: no secret value appears in the templates, the parameters or
  app configuration, and the same identities resolve `${keyvault:...}` references at run time
  (`SQLFLOW_AZURE_AUTH=mi`). Every run is given the values its flows read by the control plane: the metadata database
  as `SQLFLOW_CATALOG_DB`, each of `sqlConnections` by its name, the landing storage as `LANDING_URL`, and
  `properties` (a further value, or a `${keyvault:...}` reference to a secret you put in the vault). A person's reads
  (a flow's probe, record read back, preview and source read, and the explorer's reads of a partition) run on the
  control plane with the same values; it never delivers. The deploying principal needs to create role assignments (Owner, or User Access
  Administrator beside Contributor), to write vault secrets (Key Vault Secrets Officer; the vault uses RBAC), and
  Contributor on the resource group, where each deployment script runs in a container instance with a storage account
  of its own.
- **SQL signs in with Entra** (`sqlAuthentication=entra`, the default): each app as its own managed identity, every
  connection string with `Authentication=Active Directory Default` and no credential. The server created here is
  administered by the setup identity alone, which grants the apps and `sqlAdmins` (people or groups, by object id)
  inside every deployment. `sqlAuthentication=password` keeps a SQL admin login (`sqlAdminPassword`) instead.
- **Bring your own SQL and network**: `existingSqlServer=<host[,port]>` points the databases at a server you
  already run (a Managed Instance FQDN, for example) instead of creating one; create the databases there yourself,
  since bootstrap refuses to create a missing one unless `ControlPlane__Bootstrap__AllowCreate` is on. The deployment
  does not administer that server, so an Entra administrator of it runs the `sqlSetupScript` output once, database
  by database. A connection to a database elsewhere is `{"secretName": "<vault secret holding its connection
  string>"}`, its access yours to grant. `infrastructureSubnetId` VNet-integrates the environment (a subnet of at
  least /27 delegated to `Microsoft.App/environments`, as a workload-profiles environment needs), which is how the apps
  reach a VNet-only Managed Instance privately. The delivery engine refuses every
  private address it is not told about, so a worker whose OSDU, storage accounts or proxy resolve to private
  addresses (Azure Private Link) needs their ranges in `privateNetworks` (`SQLFLOW_DELIVERY_PRIVATE_NETWORKS`, CIDR
  ranges, comma separated). Loopback, link-local and cloud metadata addresses stay unreachable whatever it lists.
- **Landing storage**: `landingStorage='{}'` creates an account with a hierarchical namespace, reached by identity
  alone (shared keys off), and its `landing` container; `{"existingAccountId": "<account resource id>",
  "container": "<container>"}` adopts an account and container you have. `referenceName` renames `LANDING_URL`. An
  adopted account reached over Azure Private Link needs a private endpoint for its `blob` or its `dfs` sub-resource,
  with that sub-resource's `privatelink` DNS zone linked to the environment's VNet: the estate reads and writes over
  whichever of the two this network reaches ([Azure Storage endpoints](../../sqlflow/docs/reference/concepts/azure-storage-endpoints.md)).
- **Repositories**: `repositories` names each flow repository the control plane syncs (`remoteUrl`, `branch`,
  `syncIntervalSeconds`, and for a private one either the estate's `githubApp` or its own `credentialSecretName`,
  with `credentialUsername` for a host that pairs the token with a username, such as Bitbucket; `useGitHubApp: false`
  keeps the App off one repository). The control plane registers them at start and keeps them as configured: the GUI
  shows them as set by the configuration, and changing one there is refused.
- **GitHub App**: `githubApp` (`appId`, `privateKeySecretName`, and `apiUrl` for GitHub Enterprise) reads every
  GitHub repository that names no token with a token the App mints for that repository alone. The control plane and
  each worker resolve it with their own identity, so the apps need no setting of their own for it.
- **The chat assistant**: with `mcpImage` and `aiFoundryName` plus `aiFoundryModelName` (a model that serves the
  Responses API), the MCP server and the Foundry account are deployed and the control plane's assistant turns on by
  itself. The control plane calls the model keylessly with its own identity (granted Cognitive Services OpenAI User
  on the account), and the model calls the MCP server, which is public for that reason, with a token the control
  plane delegates to that one run. `aiContextEnabled=true` also turns on `!` questions, which run read-only queries
  over the approved AI contexts on the worker pool.

## Azure Container Apps with Terraform: `terraform/`

The same estate as `bicep/main.bicep`, as Terraform: the same resources, names, secrets and wiring, one module per
tier (`modules/control-plane`, `worker`, `gui`, `mcp`, `ai-foundry`, `entra-app`) and a root that composes them
into an existing resource group. Every `main.bicep` parameter has a variable of the same meaning in snake_case
(`variables.tf`); `terraform.tfvars.example` shows a typical estate.

**SQL is optional and passwordless by default.**

- Without `existing_sql_server`, an Azure SQL server is created here with the metadata database and each database
  `sql_connections` names (`OSDU_DATA_DB = { database = "OsduData" }`; `access = "read"` gives the worker read alone,
  `create = false` names one that exists already). The server is administered by the estate's setup identity, which
  creates the apps' users and makes `sql_admins` owners of every database inside every deployment.
- `existing_sql_server = "<host[,port]>"` points the estate at a SQL Server or Azure SQL Managed Instance whose
  databases already exist: nothing SQL is created, only the connection secrets are written. Its private FQDN gets port
  1433; a public endpoint carries its own (`,3342`). For a VNet-only instance, set `infrastructure_subnet_id` too.
  The deployment does not administer that server, so an Entra administrator of it runs
  `terraform output -raw sql_setup_script` once, database by database (every statement can run again); until then
  the control plane retries its first connection.
- Either way the apps sign in as their own managed identities (`sql_authentication = "entra"`, the default):
  connection strings carry `Authentication=Active Directory Default` and no credential. The control plane owns the
  catalog and the `osdu` schema; the worker reaches the `osdu` schema only, and creates, loads and reads the flows'
  tables in each data database it writes.
- `sql_authentication = "password"` keeps a SQL admin login (`sql_admin_password`) instead.
- A connection to a database elsewhere is `{ secret_name = "<vault secret holding its connection string>" }`.

**The images are this repository's, at `image_tag`.** An app whose image variable is empty takes
`<registry>/osdu-delivery-<app>:<image_tag>` from the estate's registry, and `mcp_enabled` turns the MCP server on the
same way. An image is set when its app is created: the apps ignore later image changes, because `deploy.ps1` rolls
them forward (and back) from there, so a later apply never takes an app back to the image it was created with.

**The partitions are `osdu_partitions`**, a map by data-partition-id (`url`, `token_url`, `scope`, `client_id`, the
optional `acl_owner`, `acl_viewer`, `legal_tag` and `properties`, and one `default = true` when there are several),
as `terraform.tfvars.example` shows. Each client secret is put in the vault by hand as `osdu-<id>-client-secret` (or
the secret `client_secret_name` names); the control plane's settings carry the `${keyvault:...}` reference, as with
the Bicep. `landing_storage`, `repositories` and `properties` are the Bicep's `landingStorage`, `repositories` and
`properties`.

Each environment (dev, test, prod) is two committed files in `terraform/environments/`, and is planned and applied
with its script, which runs `terraform init` against its state, `plan` with its variables and the commit's
`image_tag`, and `apply` once the plan is accepted (next section):

```powershell
.\deploy-dev.ps1 -Infrastructure -WhatIf   # the plan only
.\deploy-dev.ps1 -Infrastructure           # plan, apply once 'dev' is typed back, then show the start-up pass
```

No secret passes through Terraform: the generated ones are written by the setup step and the others by a person, so
none is in state. State holds the connection strings (no credential in them under Entra authentication; under
password authentication, the SQL password), so it still lives in the `azurerm` backend each environment's
`.backend.hcl` names, in a storage account only the deploying identities can read; never commit a state or plan file,
or a `.tfvars` file other than an environment's, which holds no secret (`.gitignore` there keeps the others out). The
deploying identity needs the same rights as for the Bicep: Contributor on the resource group, create role
assignments, write vault secrets (Key Vault Secrets Officer), and, with `provision_entra_app`, directory write for the
app registration (where it may not have it, set `provision_entra_app = false` and `azure_ad_client_id` to an existing
registration).

An estate made by an earlier version of this configuration moves to this one with a plain apply: the secrets it wrote
from variables stay in the vault (the configuration forgets them without deleting them, and the setup step keeps a
secret it finds), its data database becomes the `OSDU_DATA_DB` connection's, and the server's administrator becomes
the setup identity, with the person who administered it in `sql_admins`.

Azure can restrict creating SQL servers per subscription and region (`ProvisioningDisabled: Provisioning is restricted
in this region`); a subscription's allowed regions are listed by
`GET /subscriptions/<id>/providers/Microsoft.Sql/locations/<region>/capabilities` (status `Available`). `location`
places the whole estate, so pick one SQL may use, or bring an existing server.

`terraform test` runs `tests/` against mocked providers, so it needs no Azure access: each tier's settings, the
generated secrets and the grants the setup steps are given, the SQL connections, repositories, landing storage and
properties every run is given, the assistant and Managed Instance switches, the setup script for an existing server,
the images at the tag and the image an apply leaves alone, and the refusals (password authentication without a
password, a connection named as an estate database, a literal secret in a property, a repository over plain HTTP, a
deployment without a partition).

## Environments: dev, test and prod

An environment is two files, committed in `terraform/environments/` or kept outside the repository (below), and
nothing else:

| File | What it holds |
| --- | --- |
| `<env>.tfvars` | The estate (`subscription_id`, `resource_group_name`, `acr_name`) and its Terraform variables: sign-in, SQL and its connections, the landing storage, the repositories, the MCP server and AI Foundry. No secret, and no OSDU partition: those are `TF_VAR_osdu_partitions`, kept outside the repository, since a partition's endpoint, client id and groups are its platform's. |
| `<env>.backend.hcl` | Where its Terraform state lives: the storage account and the blob (`osdu-delivery-<env>.tfstate`). |

Fill in every `<placeholder>` once; the scripts refuse to run while one is left, naming the file and line. The
resource group, the registry in it and the state storage account are created once, by hand, before the first
`-Infrastructure`; everything else is the Terraform's.

**Kept outside the repository.** An environment whose values should not be committed leaves the repository's two
files as their placeholders and keeps its own copies elsewhere. On a workstation, `OSDU_ENVIRONMENT_DIR` names the
directory holding its `<env>.tfvars` and `<env>.backend.hcl`, and the scripts read them from there. For the Deploy
workflow, the GitHub environment's `OSDU_ENVIRONMENT_TFVARS` and `OSDU_ENVIRONMENT_BACKEND` variables hold the two
files whole (they hold no secret, so they are variables, and the run's log shows them as they are);
`tools/github/connect-environment.ps1`, run with `OSDU_ENVIRONMENT_DIR` set, sets them. The repository's dev, test and
prod files are placeholders: each environment keeps its values this way.

**The scripts.** `deploy-dev.ps1`, `deploy-test.ps1` and `deploy-prod.ps1` (at the repository root, with
`deploy-prod.bat` for cmd) are `deploy.ps1` with `-Environment`. They take every other argument of `deploy.ps1`:

```powershell
.\deploy-dev.ps1                        # build and roll out the apps whose images changed since what dev serves
.\deploy-test.ps1 control-plane worker  # exactly those apps
.\deploy-prod.ps1 -WhatIf               # what would be deployed, nothing built or changed
.\deploy-dev.ps1 -Infrastructure        # dev's Terraform: plan, then apply once 'dev' is typed back
```

- **Built once per commit.** The image tag is the commit's short SHA. An image the registry already holds at that tag
  is reused, so promoting a commit to an environment that shares the registry builds nothing.
- **Only what changed.** An app is deployed when its build inputs changed since the commit it serves; naming apps
  deploys them anyway.
- **Checked and rolled back.** Each app's new revision must serve the new image, healthy, or the app goes back to the
  image it ran, and the script fails.
- **Committed only.** A deploy, or a plan, from a tree with uncommitted changes in what it builds is refused
  (`-Force` to go ahead regardless).
- **No secret reaches Terraform.** The partitions (`TF_VAR_osdu_partitions`, the `osdu_partitions` map as JSON, with
  no secret in it) come from the process environment or from the git-ignored `.sqlflow/deploy/<env>.env` (a
  `NAME=value` line). A variable an earlier version read (`TF_VAR_jwt_signing_key`, `TF_VAR_admin_password`,
  `TF_VAR_node_token`, `TF_VAR_git_token`, `TF_VAR_osdu_partition_client_secrets`) is dropped, naming where its value
  lives now.
- **Ready when it ends.** After an apply the script waits for the control plane's start-up pass, signs in as the
  initial admin with the password the deployment generated, and shows each start-up step, each repository's sync and
  the credential it reads with, and whether the chat assistant is on. When the environment names a GitHub App
  (`github_app`), it first checks the App's private key is in the vault, and prints the command that puts it there
  when it is not.

**A new environment**, start to end: create the resource group, the registry and the state account; fill in its
files; `.\deploy-<env>.ps1 -Infrastructure` (it builds the images of the apps it creates); put each partition's client
secret, and the GitHub App's private key or each private repository's token, in the vault, under the names the outputs
and the environment's file give.
The control plane picks them up on its next attempt, with no redeploy. From then on, `.\deploy-<env>.ps1` deploys.

### Deploying from GitHub

A repository deploys one way, and the repository variable `DEPLOY_METHOD` says which: the **Deploy** workflow
(`.github/workflows/deploy.yml`) runs only when it is `terraform`, and runs the same `deploy.ps1`. A fork that deploys
with workflows of its own leaves `DEPLOY_METHOD` unset and sets `CI_ENABLED` to `false`, keeping both workflow files as
they are so its syncs do not conflict on them ([CONTRIBUTING.md](../../CONTRIBUTING.md#forks)):

- a push to `main` deploys to **dev** once CI has passed on it;
- **test** and **prod** are promoted by running the workflow by hand from `main` (Actions > Deploy > Run workflow),
  choosing the environment, optionally the apps, and whether to plan and apply the environment's Terraform first;
- **prod** waits for the reviewers its GitHub environment names.

Each GitHub environment (`dev`, `test`, `prod`) signs in to Azure as its own user-assigned managed identity, trusted
for that GitHub environment only (OIDC: no secret). `tools/github/connect-environment.ps1 -Environment <env>` creates
it in the environment's resource group (idempotent, `-WhatIf` to see first), with its federated credential and roles,
and sets the GitHub environment's `AZURE_CLIENT_ID` and `AZURE_TENANT_ID` (and, for an environment kept outside the
repository, `OSDU_ENVIRONMENT_TFVARS` and `OSDU_ENVIRONMENT_BACKEND`):

| Role | On | For |
| --- | --- | --- |
| Contributor | the environment's resource group | the apps, the registry's builds, everything the Terraform creates |
| Role Based Access Control Administrator | the environment's resource group | the role assignments the Terraform gives the apps' identities |
| Key Vault Secrets Officer | the environment's resource group | the connection strings the Terraform writes in the vault, and the initial admin's password `deploy.ps1` signs in with |
| Storage Blob Data Contributor | the state storage account | the environment's Terraform state |

The GitHub environment holds one secret, `TF_VAR_OSDU_PARTITIONS` (the partitions, with no secret in them); only the
run that plans and applies the Terraform reads it. A managed identity has no right in
the directory, so for the Terraform to create the sign-in registration it needs Microsoft Graph's
`Application.ReadWrite.OwnedBy`, granted by a directory administrator; otherwise the environment uses an existing
registration (`provision_entra_app = false`).

## Kubernetes: `k8s/`

Prerequisites: an ingress controller (the annotations assume ingress-nginx), cert-manager or another TLS source,
and [KEDA](https://keda.sh) for node autoscaling.

```bash
kubectl apply -f osdu/deploy/k8s/namespace.yaml
# create the real secret (see secrets.example.yaml for the required keys), then:
kubectl apply -f osdu/deploy/k8s/controlplane.yaml
kubectl apply -f osdu/deploy/k8s/gui.yaml
kubectl apply -f osdu/deploy/k8s/ingress.yaml
# mint the node token, add it to the secret, then:
kubectl apply -f osdu/deploy/k8s/worker-pool.yaml
# optional: the MCP server, on the same host under /mcp
kubectl apply -f osdu/deploy/k8s/mcp.yaml
```

The layout and the reasoning behind it:

- **One host, path split** (`/api` and `/openapi` to the control plane, `/` to the GUI): the SPA runs same-origin
  with the API, so no CORS configuration exists anywhere. The GUI image's `SQLFLOW_API_BASE_URL=""` means "same
  origin".
- **The MCP server is one more path of that host** (`/mcp`, in `mcp.yaml` with an ingress of its own, so applying
  or deleting that one file adds or removes it). It is stateless and holds no secret: a client sends its own token
  on every request, and the server forwards it to the control plane inside the cluster.
- **The control plane runs one replica, API-only** (`ControlPlane__Worker__Enabled=false`). A rolling update
  briefly overlaps two replicas; the old one releases the dispatch lease on its graceful stop and the nodes retry.
- **Forwarded headers are trusted from the ingress only** (`ControlPlane__Proxy__*`): set `KnownNetworks` to your
  cluster's ingress/pod CIDR. Without it, rate limiting and login throttling key on the ingress address instead of
  the real client.
- **Nodes scale 0 to N per pool on the control plane's replica target**: KEDA's metrics-api scaler reads
  `GET /api/v1/node/scale-target?pool=<name>` with the node token and scales the matching Deployment;
  `minReplicaCount: 0` means an idle pool costs nothing. Copy `worker-pool.yaml` per pool (set
  `SQLFLOW_WORKER_POOL` and the URL's `?pool=`).
- **The partition is the control plane's configuration**: `controlplane.yaml` names one, `dev`, as
  `Osdu__Partitions__dev__...` variables with example values to replace (rename `dev` to your data-partition-id).
  Its client secret is the reference `${env:OSDU_CLIENT_SECRET}`, which the control plane and the worker pods
  resolve from the `osdu-client-secret` key they both mount; on a cluster whose pods sign in to Azure,
  `${keyvault:<vault>/<secret>}` reads it from a Key Vault instead.
- **Secrets stay on the tier that uses them**: the control plane gets the metadata connection and JWT material,
  and the flows' `${env:...}` references, which a person's reads of a flow (a probe, a record read back, a preview,
  a source read) and the explorer's reads of a partition resolve there as a run resolves them on a node; both get the git credentials (the
  GitHub App's ID and key, and the git token) for a source naming none; nodes get a node token, a connection to the
  `osdu` schema of their own (mounted as `SQLFLOW_CATALOG_DB`),
  the data connection (`OSDU_DATA_DB`) and every other `${env:...}` reference their pool's flows use, and never the
  catalog's own credential. Keep `controlplane.yaml`'s flow references and the pools' the same. The control plane
  reads; nothing it does delivers, and no run passes through it.

## Radix: `radix/`

Radix runs KEDA but builds its rules itself and has no trigger that reads the control plane's replica target, and it
gives every pod a 30 second grace period that an application cannot raise. So on Radix the control plane manages its
workers itself ([../docs/reference/guides/managed-workers.md](../docs/reference/guides/managed-workers.md)): the
workers are a Radix **job component**, and the control plane starts one job per worker through the component's job
scheduler, keeps every managed pool at its minimum, starts more for queued runs no free slot covers, and tells an
idle extra worker to leave once it holds nothing. Nothing is ever scaled away under a run.

`radix/radixconfig.yaml` is the application in the shape of the estate's other Radix apps. Its GitHub workflow,
`radix/radix-deploy.workflow.yml`, is a sample: GitHub runs only the workflows under `.github/workflows`, so a
repository that deploys with Radix copies it there and sets the repository variable `DEPLOY_METHOD` to `radix` (which
also keeps the Terraform Deploy workflow from running). It builds the four images, pushes them to the registry, and
rolls them out with a deploy-only pipeline (`rx create pipeline-job deploy`) pinned to the commit. What to set up per
environment (identities, Azure SQL, Key Vault, sign-in, AI Foundry, network) is in
[../docs/reference/guides/radix.md](../docs/reference/guides/radix.md).

- **`controlplane`** is one replica with `ControlPlane__ManagedWorkers__Launcher=radix` and the job scheduler's
  address, `http://worker:8080`, which only this application's components can reach and which needs no credential.
  Each environment names its OSDU partition here (`Osdu__Partitions__dev__...` in dev, `test` and `prod` in the
  others: rename each to the environment's data-partition-id), with the client secret as
  `${keyvault:<key vault>/osdu-client-secret}`, which the worker resolves with its workload identity. It reaches
  `OsduDelivery` as `SQLFLOW_CATALOG_DB` and `OsduData` as `OSDU_DATA_DB`.
- **`gui`** calls the control plane's public address (`SQLFLOW_API_BASE_URL`), and the control plane allows the
  GUI's origin (`ControlPlane__Cors__AllowedOrigins__0`): Radix gives each public component its own host, so the
  path split of the other layouts does not exist here.
- **`mcp`** is the MCP server, public because the chat assistant's model calls it from AI Foundry; it calls the
  control plane inside the application (`http://controlplane:8080`) with each request's own token.
- **`worker`** is the job component. Its variables and Key Vault references (the node token, the `osdu` schema
  connection, `SQLFLOW_CATALOG_DB` and `OSDU_DATA_DB`) are every worker's, and it holds no OSDU value of its own:
  those come with each run, from its partition. The control plane adds only each job's name, pool, drain window, time
  limit and resources when it starts it. The component's `safeToRestart: false` keeps the cluster autoscaler from
  moving a worker, and `backoffLimit: 0` leaves replacing a worker that ends to the control plane.

The node token is a secret of the environment's Key Vault, `node-token`, generated before the first deploy in SQLFlow's
token form, as the Terraform estate's setup step generates it: the control plane makes it the admin's node credential
when it starts (`ControlPlane__Bootstrap__NodeTokenReference`), and every worker presents it, so the first worker
starts with the first deploy. The flow repository is configured the same way
(`ControlPlane__Bootstrap__Repositories__<name>__...`, with its token as a vault reference, or a
`${githubapp:app=<client ID>;key=keyvault:<vault>/<secret>}` reference to a read-only GitHub App). A redeploy leaves running
jobs alone; the new control plane replaces every worker from before it once their replacements poll, so no run is cut
off by the rollout.

## Scale-in must not sever a delivery

A node handles SIGTERM by draining: it stops taking work and lets the runs it already holds finish and record
their outcomes, for up to `SQLFLOW_WORKER_DRAIN_SECONDS` (default 540). That only works if the orchestrator waits,
and both Kubernetes and Container Apps default to a 30 second grace period. **Set the termination grace period
above the drain window on every node workload**: `worker-pool.yaml` and `worker.bicep` both set 600. Left at the
default, the drain starts and is killed 30 seconds in, every unfinished run is severed with no outcome recorded,
and each severed run consumes one of its execution attempts.

## Placement reminder

Nodes must run where they can reach the data database and the OSDU endpoint their pool's flows touch (that is
the point of pools). A cloud cluster's nodes serve cloud-reachable data; an on-prem pool means nodes on-prem
(compose, systemd, or a local cluster) pointed at the same control plane, registered under that pool name.
