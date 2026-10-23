---
id: delivery-guide-radix-setup
title: "Setting up a Radix environment by hand: the Azure resources, users and secrets the Radix deploy does not create"
type: guide
summary: "What a person sets up once per Radix environment before the first deploy: identities, SQL users, vault secrets, partition, repository, sign-in, network."
keywords:
  - radix setup
  - radix checklist
  - manual steps
  - before the first deploy
  - workload identity
  - federated credential
  - key vault secrets
  - node-token
  - jwt-signing-key
  - azure sql users
  - osdu schema
  - radixconfig placeholders
  - private endpoint
related:
  - delivery-guide-radix
  - delivery-guide-managed-workers
  - delivery-concept-partitions
  - delivery-guide-deployment
  - guide-chat-assistant
  - concept-authentication-and-identity
  - guide-github-app
  - concept-azure-storage-endpoints
sourceRefs:
  - osdu/deploy/radix/radixconfig.yaml
  - osdu/deploy/radix/radix-deploy.workflow.yml
  - osdu/deploy/terraform/main.tf
  - osdu/src/SqlFlow.Delivery/Catalog/ConfiguredPartitions.cs
  - sqlflow/src/SqlFlow.ControlPlane/Background/BootstrapProvisioningService.cs
  - sqlflow/src/SqlFlow.ControlPlane/Security/AccessTokenGenerator.cs
---

# Setting up a Radix environment by hand

The Terraform estate ([Deployment](deployment.md)) creates everything OSDU Delivery needs and generates its secrets.
Radix runs only the application, so what a Terraform apply does around it is done once per environment by a person,
before the first deploy. After it, nothing is set up by hand: the control plane reads its configuration from
[radixconfig.yaml](../../../deploy/radix/radixconfig.yaml) when it starts, registers the partition and the flow
repository, and adopts the workers' credential. [Deploying on Radix](radix.md) is what the application is and how it
is deployed; this page is the checklist.

| Step | What a person does | Named in `radixconfig.yaml` as |
| --- | --- | --- |
| [1](#1-the-radix-application-and-the-deploy-identity) | Register the application; give the deploy identity its rights; set the registry's pull password | `<registry>` |
| [2](#2-two-workload-identities) | Create two identities with a federated credential each | `<env control plane identity client id>`, `<env worker identity client id>` |
| [3](#3-azure-sql) | Create two databases and the identities' users and roles | `<env sql server>` |
| [4](#4-key-vault) | Create a vault with its five secrets; let both identities read it | `<env key vault>` |
| [5](#5-the-osdu-partition) | Name the OSDU partition the environment delivers to | `Osdu__Partitions__<id>__...` |
| [6](#6-the-flow-repository) | Name the flow repository and store its token | `ControlPlane__Bootstrap__Repositories__estate__...` |
| [7](#7-sign-in) | Create the app registration people sign in with | `<env sign-in app registration client id>`, `<tenant id>` |
| [8](#8-ai-foundry) | Create a Foundry project and model; let the control plane call it | `<env foundry account>`, `<env project>`, `<env model deployment>` |
| [9](#9-network) | Admit the cluster to SQL, the vault and Foundry | `SQLFLOW_DELIVERY_PRIVATE_NETWORKS` |
| [10](#10-capacity) | Check the environment's quota against the worker pool | `ControlPlane__ManagedWorkers__MaxWorkers` |
| [11](#11-fill-in-the-file-and-the-repository) | Replace every placeholder; set the GitHub variables and secrets; copy the workflow | every `<...>` |

Every value in angle brackets in `radixconfig.yaml` is the estate's own. Radix refuses a client id that is not a
GUID, so a forgotten placeholder stops the deploy rather than a running component; a forgotten host name or group
name does not, so the file is checked line by line in step 11.

## 1. The Radix application and the deploy identity

1. Register `osdu-delivery` in the Radix web console with this repository and the config file path
   `osdu/deploy/radix/radixconfig.yaml`.
2. Make the deploy identity (the Entra app or managed identity GitHub signs in as) an administrator of the
   application, and give it `AcrPush` on the registry the file names (`privateImageHubs`). Add a federated credential
   on it for this repository's GitHub environments (`repo:<owner>/osdu-delivery:environment:dev`, and `test`, `prod`),
   and set `AZURE_CLIENT_ID` and `AZURE_TENANT_ID` as repository or environment secrets.
3. In the application's Radix console, set the password of the registry's `radix-pull` user (`privateImageHubs`).

## 2. Two workload identities

The control plane and the workers each sign in to Azure as their own identity (a user-assigned managed identity or an
app registration), set as `identity.azure.clientId` per environment:

| Identity | Federated credential subject | Needs |
| --- | --- | --- |
| control plane | `system:serviceaccount:osdu-delivery-<env>:controlplane-sa` | Azure SQL (step 3), `Key Vault Secrets User` on the environment's vault (step 4), `Cognitive Services OpenAI User` on the Foundry account (step 8) |
| worker | `system:serviceaccount:osdu-delivery-<env>:worker-sa` | Azure SQL (step 3), `Key Vault Secrets User` on the environment's vault (step 4) |

The issuer of each federated credential is the Radix cluster's OIDC issuer; the Radix API's configuration
(`GET /api/v1/configuration` on the cluster's Radix API, signed in) lists it as `clusterOidcIssuers`. The application
signs in through Azure's default credential chain, which picks up the workload identity Radix gives the pod; nothing
else selects it, and `SQLFLOW_AZURE_AUTH` stays unset.

## 3. Azure SQL

Two databases on one server: `OsduDelivery` holds SQLFlow's catalog and the `osdu` schema beside it, and `OsduData`
holds the ingestion tables the flows load and deliver from. Keep both names: the estate's flows name their tables
through them (`OsduData.arc.WellLog`, `OsduDelivery.osdu.v_dim_WellLogCurve`). Both must exist; the control plane
migrates the catalog and the `osdu` schema itself when it starts, and creates neither database. Both identities sign in
with `Authentication=Active Directory Default`, so no connection carries a password. As the server's Entra
administrator, create a user for each from its identity's name, with the same roles the Terraform estate grants:

```sql
-- In OsduDelivery
CREATE USER [<control plane identity>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_owner ADD MEMBER [<control plane identity>];       -- it migrates the catalog and the osdu schema

-- The worker's grants name the osdu schema, which the control plane's first migration creates only when it is
-- missing: create it here first, so the grants hold before the first deploy.
IF SCHEMA_ID(N'osdu') IS NULL EXEC(N'CREATE SCHEMA [osdu];');
CREATE USER [<worker identity>] FROM EXTERNAL PROVIDER;           -- a worker reaches the osdu schema only
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE, ALTER, REFERENCES ON SCHEMA::osdu TO [<worker identity>];
GRANT CREATE TABLE, CREATE VIEW TO [<worker identity>];           -- dimension builds create osdu.dim_* and osdu.v_dim_*

-- In OsduData
CREATE USER [<control plane identity>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<control plane identity>];   -- a person's reads of a flow

CREATE USER [<worker identity>] FROM EXTERNAL PROVIDER;           -- the flows create, widen, load and read their tables
ALTER ROLE db_ddladmin ADD MEMBER [<worker identity>];
ALTER ROLE db_datareader ADD MEMBER [<worker identity>];
ALTER ROLE db_datawriter ADD MEMBER [<worker identity>];
```

## 4. Key Vault

One vault per environment, named in `radixconfig.yaml` (`<dev key vault>` and so on), with RBAC authorization, both
identities as `Key Vault Secrets User`, and these secrets:

| Secret | Read by | How | Content |
| --- | --- | --- | --- |
| `jwt-signing-key` | control plane | `secretRefs`: mounted as `ControlPlane__Jwt__SigningKey` | At least 32 random bytes (for example 64 hex characters). It signs every session; changing it signs everyone out. |
| `admin-password` | control plane | `${keyvault:...}` reference | The bootstrap `admin` account's password. |
| `node-token` | control plane, worker | `${keyvault:...}` reference: `ControlPlane__Bootstrap__NodeTokenReference` and the job component's `SQLFLOW_TOKEN` | The workers' credential, in SQLFlow's token form: `sqlf_` followed by at least 32 random characters (the generator below writes 43). The control plane makes it the admin's `node`-scope token when it starts, revoking the one it replaced, so rotating the secret and restarting the control plane rotates the credential. |
| `osdu-client-secret` | worker, control plane | `${keyvault:...}` reference: the partition's `OSDU_CLIENT_SECRET` value | The secret of `OSDU_CLIENT_ID`, the client the flows sign in to OSDU with. A worker reads it when a run needs it, and the control plane for the reads a person waits on. |
| `git-token` | control plane, worker | `${keyvault:...}` reference: the repository's `CredentialReference` | A token that can read the flow repository (for GitHub, a fine-grained token with read access to the repository's contents): the control plane syncs with it, a worker checks out the commit a run is pinned to. A public repository needs none, and one read with a GitHub App needs none either. |
| `github-app-private-key` | control plane, worker | `${keyvault:...}` reference inside the repository's `CredentialReference` (`${githubapp:app=...;key=keyvault:...}`) | Instead of `git-token`, for a GitHub repository: the private key (the `.pem` file, as GitHub generated it) of a GitHub App with read-only access to the repository's contents, installed on it ([creating one](../../../../sqlflow/docs/reference/guides/github-app.md)). Each sync and each checkout mints a token for the repository alone, so no token is stored. |

The secret `secretRefs` mounts must exist before the component's pod starts; the ones read through a `${keyvault:...}`
reference are read by the application itself when it needs them, and a missing one is reported by the control plane's
start-up pass (`GET /api/v1/bootstrap`) or the worker's log rather than by a pod that does not start.

The three secrets nobody has to know are generated, never typed. With `openssl` and the Azure CLI:

```bash
vault=<env key vault>
az keyvault secret set --vault-name "$vault" --name jwt-signing-key --value "$(openssl rand -hex 32)" --output none
az keyvault secret set --vault-name "$vault" --name admin-password  --value "$(openssl rand -base64 24)" --output none
az keyvault secret set --vault-name "$vault" --name node-token \
  --value "sqlf_$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=')" --output none
```

The same in PowerShell:

```powershell
$vault = '<env key vault>'
function Random-Bytes([int] $count) { $b = [byte[]]::new($count); [System.Security.Cryptography.RandomNumberGenerator]::Fill($b); $b }
az keyvault secret set --vault-name $vault --name jwt-signing-key --value ([Convert]::ToHexString((Random-Bytes 32))) --output none
az keyvault secret set --vault-name $vault --name admin-password  --value ([Convert]::ToBase64String((Random-Bytes 24))) --output none
az keyvault secret set --vault-name $vault --name node-token `
  --value ('sqlf_' + [Convert]::ToBase64String((Random-Bytes 32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')) --output none
```

`osdu-client-secret` and `git-token` are the values their owners hand over, set the same way. Read `admin-password`
back when a person needs it (`az keyvault secret show --vault-name <vault> --name admin-password --query value -o tsv`).

## 5. The OSDU partition

Each environment's `controlplane` variables name the OSDU partition that environment delivers to, as the control plane's
configuration ([configured partitions](../concepts/partitions.md#configured-partitions)):
`Osdu__Partitions__<data-partition-id>__Default`, `__Description`, and one `__Properties__<NAME>` per value its runs
resolve. The file names the ones every delivery reads:

| Property | Value |
| --- | --- |
| `OSDU_URL` | The OSDU platform's address. |
| `OSDU_TOKEN_URL`, `OSDU_SCOPE`, `OSDU_CLIENT_ID` | How the flows sign in to it. |
| `OSDU_CLIENT_SECRET` | The text `${keyvault:<env key vault>/osdu-client-secret}`, never the value: the control plane refuses to start with a secret's value here. |
| `OSDU_LEGAL_TAG` | The legal tag every delivered record carries unless its flow sets its own. |
| `OSDU_ACL_OWNER`, `OSDU_ACL_VIEWER` | The access groups every delivered record carries unless its flow sets its own ([the references the delivery kind supplies](../concepts/environment-variables.md#references-the-delivery-kind-supplies)). |
| `OSDU_LOGTYPE_ACL_OWNER`, `OSDU_LOGTYPE_ACL_VIEWER` | The groups a flow spells by its own name, here the LogType flow's. Add one such property per `${env:...}` reference the estate's flows use that no row above gives. |

The control plane registers the partition when it starts; the workers hold no OSDU value of their own, since every run
carries its partition's. Rename the partition in the file to the environment's own data-partition-id; a second partition
is another set of these under its own name, with exactly one `Default`.

## 6. The flow repository

The repository the flows live in is a setting of each environment's `controlplane`, registered at start and kept as
set there (`ControlPlane__Bootstrap__Repositories__estate__RemoteUrl`, `__Branch`, `__CredentialReference`; rename
`estate` to the repository's own name). Its token is `git-token` in the vault (step 4), as the reference
`${keyvault:<env key vault>/git-token}`; a public repository leaves the reference out. A GitHub repository can be read
with a GitHub App instead: the reference names the App and where its key is kept,
`${githubapp:app=<the App's client ID>;key=keyvault:<env key vault>/github-app-private-key}`, and the App needs only
read access to the repository's contents ([creating one](../../../../sqlflow/docs/reference/guides/github-app.md)). The control plane syncs the repository on its
interval, and a worker that runs a pinned commit checks it out with the same reference, resolved with the worker's own
identity.

## 7. Sign-in

An Entra app registration per environment with a single-page application redirect URI of the GUI's address
(`https://gui-osdu-delivery-<env>.<zone>`); its client id is `ControlPlane__AzureAd__ClientId`. Sign-in is open to
the tenant `ControlPlane__AzureAd__AllowedTenantIds__0` names; to limit it to a group, require assignment on the
registration's enterprise application and assign the group. A first-time user is given the `viewer` role; an admin
raises it in the GUI.

## 8. AI Foundry

A Foundry project with a model deployment that serves the Responses API; the control plane calls
`https://<account>.openai.azure.com/openai/responses` with its workload identity (no key), so the account's name is
the first label of `ControlPlane__Assistant__Foundry__ProjectEndpoint`. Grant the control plane identity
`Cognitive Services OpenAI User` on the account. The model calls the MCP server at its public address with a token
the control plane delegates to that one run, so the MCP server must stay public. Questions starting with `!` run
read-only queries over the approved AI contexts on the workers, which therefore need the connections those contexts
open (`OSDU_DATA_DB` already). An environment without the assistant sets `ControlPlane__Assistant__Enabled` to `false`
and leaves the Foundry placeholders alone.

## 9. Network

Radix lets components reach the internet unless `egress` rules are set, and the file sets none: a rule list blocks
every address it does not name, including Entra sign-in. Azure SQL, Key Vault and Foundry each need to admit the
cluster, in one of two ways:

- **Private endpoint**: request one per resource for the cluster with the private link form in the Radix platform
  repository (sub-resource `sqlServer`, `vault`, `account`), then approve the connection on the resource. Your
  subscription must be allowed to have endpoints in Radix's first.
- **Firewall**: admit the cluster's egress addresses, which the Radix API's configuration lists as
  `clusterEgressIps`.

The landing storage account admits the cluster the same two ways. Its private endpoint can be for its `blob` or its
`dfs` sub-resource, with that sub-resource's `privatelink` DNS zone: the estate reads and writes the account over
the one that resolves to the private endpoint, and the other answering `403 AuthorizationFailure` from its public
address is then expected ([Azure Storage endpoints](../../../../sqlflow/docs/reference/concepts/azure-storage-endpoints.md)).

An OSDU, storage account or proxy that resolves to a private address must also be listed in
`SQLFLOW_DELIVERY_PRIVATE_NETWORKS` (both on the control plane and on the worker); empty, a flow reaches public
addresses only.

## 10. Capacity

The control plane keeps the default pool between `ControlPlane__ManagedWorkers__MinWorkers` and `__MaxWorkers`
workers (1 and 10 in the file), each a job asking for the CPU and memory under
`ControlPlane__ManagedWorkers__Radix__Resources` (1 to 2 CPU and 2 to 4 GiB in the file). While workers are replaced
(a rollout, or workers older than `MaxLifetimeHours`), the new ones run beside the old ones finishing their runs, so the
environment's quota must allow up to twice `MaxWorkers` jobs at their limits, beside the control plane, the GUI and the
MCP server. Lower `MaxWorkers` or the resources before the first deploy if it does not.

## 11. Fill in the file and the repository

1. Replace every value in angle brackets in `radixconfig.yaml` for each environment the application has; every
   address in it carries the DNS zone of the cluster the application is registered on (`<radix cluster domain>`).
2. Set the repository variables `DEPLOY_METHOD` to `radix` (which also keeps the Container Apps workflow from running)
   and `RADIX_REGISTRY_NAME` to the registry, and the secrets `AZURE_CLIENT_ID` and `AZURE_TENANT_ID` (step 1).
3. Copy `osdu/deploy/radix/radix-deploy.workflow.yml` to `.github/workflows/radix-deploy.yml`.

Then deploy ([Deploying on Radix](radix.md#deploy)). The first start registers the partition and the repository and
adopts `node-token`; sign in as `admin` with `admin-password`, and the Nodes page shows the first worker polling.
