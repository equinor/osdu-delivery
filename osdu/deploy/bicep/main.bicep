// The full OSDU Delivery estate on Azure Container Apps in one resource-group deployment: Log Analytics and the
// Container Apps environment, a Key Vault holding every secret, the Azure SQL databases the estate has (OsduDelivery,
// the metadata: SQLFlow's catalog and the osdu schema; and each of sqlConnections, the data the flows land, load and
// deliver from), a landing storage account for the files the flows read, and the apps composed from the per-tier
// templates in this directory:
//
//   gui.bicep             the SPA, external ingress; calls the control plane cross-origin
//   control-plane.bicep   the API (in-process worker OFF: the API replica does API work only), CORS'd to the GUI
//   worker.bicep          one worker pool, no ingress, scaled 0..N on the control plane's replica target
//   mcp.bicep             optional (mcpEnabled or mcpImage): the MCP server, external ingress, the assistant's tools
//   ai-foundry.bicep      optional (aiFoundryName): an AI Foundry account, project and model for the assistant
//   entra-app.bicep       optional (provisionEntraApp): the Entra app registration users sign in with
//   landing-access.bicep  with landingStorage: the apps' access to it, granted in the account's own resource group
//
// With both the MCP server and a Foundry model, the GUI chat assistant turns on by itself: the control plane calls
// the model keylessly with its own identity, and the model calls the MCP server with a token the control plane
// delegates to that one run.
//
// The configuration is the only description of the estate (osdu/docs/reference/guides/deployment.md), and this
// template deploys the same estate as osdu/deploy/terraform: the same resources, names, secrets, settings and grants.
// It creates the resources and the identities, and gives the control plane its configuration: the partitions, the
// values every run resolves with (the SQL connections, the landing storage, any further property), the repositories to
// sync, and the node credential. The control plane applies it at every start (its start-up pass, GET /api/v1/bootstrap)
// and hands every run its values, so a worker carries its identity and nothing else. Data reaches OSDU through three
// flows in lineage order: a pre-ingestion flow lands the source files in a data database, an ingestion flow loads the
// keyed ingestion tables there, and the OSDU flow reads those tables and delivers. A flow names each database by the
// name it spells (${env:OSDU_DATA_DB}, ${env:SQLFLOW_CATALOG_DB}), so a flow document moves from test to prod
// unchanged.
//
// No secret is a parameter (but sqlAdminPassword, under SQL password authentication alone), and none passes through
// the deployment: the deployment's setup identity generates the ones nobody provides (the JWT signing key, the initial
// admin's password, the node credential) into the vault when they are missing (osdu/deploy/setup/Setup-Estate.ps1, run
// as a deployment script), and the ones only a person has (each partition's OSDU client secret, a private repository's
// token, the connection string of a database elsewhere) are put in the vault by hand and named here; the control plane
// reports one it cannot read as a failed start-up step. The same setup identity administers the SQL server created
// here (Entra-only authentication, no SQL login) and creates the apps' database users inside every deployment, so a
// deployment needs no step by hand. Each app's user-assigned managed identity reads its secrets at start and resolves
// ${keyvault:...} references at run time. The deploying identity needs Owner (or Contributor and User Access
// Administrator) on the resource group: it creates role assignments, and each deployment script runs in a container
// instance and a storage account of its own there.
//
// Build and push the images first (see osdu/deploy/README.md), then, with the partitions in a file
// (partitions.json: [{ "id": "dev", "url": "https://<platform host>", "tokenUrl": "...", "scope": "...",
// "clientId": "..." }]):
//
//   az deployment group create -g <rg> -f main.bicep \
//     -p acrName=<registry> imageTag=<commit the images were built from> \
//        osduPartitions=@partitions.json \
//        sqlAdmins='[{"name": "<group display name>", "objectId": "<group object id>", "type": "group"}]' \
//        repositories='{"flows": {"remoteUrl": "https://github.com/<owner>/<repository>.git",
//                                 "credentialSecretName": "github-flows-token"}}' \
//        landingStorage='{}'
//
// and for the chat assistant, add
//
//        mcpEnabled=true aiFoundryName=<globally-unique-name> aiFoundryModelName=<model, e.g. gpt-5.1>
//
// Then a person with Key Vault Secrets Officer on the vault (the keyVaultName output) puts each partition's client
// secret there under the name the osduPartitions output gives it, and each private repository's token under its
// credentialSecretName:
//
//   az keyvault secret set --vault-name <keyVaultName> --name osdu-dev-client-secret --value <client secret>
//
// On first start, bootstrap provisioning applies SQLFlow's catalog migrations and then the OSDU module's, and creates
// the admin user, so the GUI is sign-in ready at the guiUrl output with the password in the vault secret the
// adminPasswordSecret output names. With an existing SQL server under Entra authentication, an Entra administrator of
// that server runs the sqlSetupScript output once instead of the grants step. Add worker pools by deploying
// worker.bicep again with a distinct name and pool.

// ------------------------------------------------------------------------------------------------------- types ---

@sealed()
@description('A person or group given db_owner in every database of the estate.')
type sqlAdmin = {
  @description('The name as Entra shows it: a user\'s sign-in name, a group\'s display name.')
  name: string

  @description('The object id of the user or group.')
  objectId: string

  @description('user (the default) or group.')
  type: ('user' | 'group')?
}

@sealed()
@description('An OSDU partition this deployment delivers to.')
type osduPartition = {
  @description('The data-partition-id: letters, digits, underscore, hyphen and dot, at most 200 characters.')
  id: string

  @description('Whether it is the partition a run that names none runs in: exactly one of several. One partition alone is the default without saying so.')
  default: bool?

  @description('What the partition is for.')
  description: string?

  @description('The platform, which its runs resolve as OSDU_URL.')
  url: string

  @description('The token endpoint, as OSDU_TOKEN_URL.')
  tokenUrl: string

  @description('The scope its tokens are asked for, as OSDU_SCOPE.')
  scope: string

  @description('The client id its runs sign in with, as OSDU_CLIENT_ID.')
  clientId: string

  @description('The vault secret a person puts its client secret in, read as OSDU_CLIENT_SECRET. Left out: osdu-<id>-client-secret, the id lower-cased with every character other than a-z and 0-9 a hyphen.')
  clientSecretName: string?

  @description('OSDU_ACL_OWNER.')
  aclOwner: string?

  @description('OSDU_ACL_VIEWER.')
  aclViewer: string?

  @description('OSDU_LEGAL_TAG.')
  legalTag: string?

  @description('Any further name a flow spells, with its value; one named as a standard value replaces it.')
  properties: { *: string }?
}

@sealed()
@description('A SQL database the estate\'s flows read and write: on the estate\'s server (database) or elsewhere (secretName), never both.')
type sqlConnection = {
  @description('A database on the estate\'s server.')
  database: string?

  @description('Create the database (the default) when this template creates the server; false when it exists already.')
  create: bool?

  @description('write (the default) lets the worker create, load and read its tables; read lets it read them. The control plane always reads.')
  access: ('read' | 'write')?

  @description('A vault secret a person puts there, holding the whole connection string of a database elsewhere.')
  secretName: string?
}

@sealed()
@description('A flow repository the control plane syncs.')
type repository = {
  @description('The remote, an https:// URL.')
  remoteUrl: string

  @description('The branch it syncs. Default main.')
  branch: string?

  @description('How often it syncs, in seconds: at least 30. Default 300.')
  @minValue(30)
  syncIntervalSeconds: int?

  @description('For a private remote read with its own token, the vault secret a person puts the token in (a GitHub fine-grained token with read access to the repository\'s contents). It wins over githubApp.')
  credentialSecretName: string?

  @description('The username paired with the token, where the host needs one.')
  credentialUsername: string?

  @description('Whether the estate\'s githubApp reads this repository when it is on the App\'s GitHub and names no credentialSecretName. Default true; false reads it with no credential (a public repository the App is not installed on).')
  useGitHubApp: bool?

  @description('Flow documents in the repository the control plane does not register.')
  excludedFlowPaths: string[]?
}

@sealed()
@description('The GitHub App the estate\'s GitHub repositories are read with, in place of a token per repository (sqlflow/docs/reference/guides/github-app.md).')
type gitHubAppOptions = {
  @description('The App\'s client ID (like Iv23liAbCdEf) or its numeric App ID.')
  appId: string

  @description('The vault secret a person puts the App\'s private key in: the .pem file GitHub generated, as downloaded.')
  privateKeySecretName: string

  @description('For GitHub Enterprise Server (https://<host>/api/v3) or GHE.com (https://api.<name>.ghe.com), the API root. Default https://api.github.com.')
  apiUrl: string?
}

@sealed()
@description('The storage account the flows read their source files from: created here or adopted.')
type landingStorageOptions = {
  @description('The account created here. Left out: osdulanding<13 characters derived from the resource group>.')
  accountName: string?

  @description('The resource id of an existing account to adopt instead of creating one; its container must exist.')
  existingAccountId: string?

  @description('The container the files are in. Default landing.')
  container: string?

  @description('The name a flow spells its location by in \${env:NAME}. Default LANDING_URL.')
  referenceName: string?
}

@description('What the deployment reports of a partition.')
type osduPartitionReport = {
  @description('Whether it is the partition a run that names none runs in.')
  default: bool

  @description('The vault secret its client secret is read from.')
  clientSecretName: string
}

// --------------------------------------------------------------------------------------------------- functions ---

// Each function stands alone (none calls another), and a name is checked character by character: every character of
// it is one of a set, compared case-sensitively.

@description('Whether name is spelt as a flow spells a name in \${env:NAME}: a letter or underscore, then letters, digits and underscores, at most 64 characters.')
func isEnvironmentName(name string) bool =>
  length(name) >= 1 && length(name) <= 64 && contains('ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_', take(name, 1)) && empty(filter(range(0, length(name)), i => !contains('ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_', substring(name, i, 1))))

@description('Whether id is a data-partition-id: letters, digits, underscore, hyphen and dot, at most 200 characters.')
func isPartitionId(id string) bool =>
  length(id) >= 1 && length(id) <= 200 && empty(filter(range(0, length(id)), i => !contains('ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_.-', substring(id, i, 1))))

@description('Whether name names a repository: letters, digits, underscore, hyphen and dot, starting with a letter or digit, at most 100 characters.')
func isRepositoryName(name string) bool =>
  length(name) >= 1 && length(name) <= 100 && contains('ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789', take(name, 1)) && empty(filter(range(0, length(name)), i => !contains('ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_.-', substring(name, i, 1))))

@description('Whether name is a blob container name: 3 to 63 lowercase letters, digits and hyphens, starting and ending with a letter or digit.')
func isContainerName(name string) bool =>
  length(name) >= 3 && length(name) <= 63 && take(name, 1) != '-' && last(name) != '-' && empty(filter(range(0, length(name)), i => !contains('abcdefghijklmnopqrstuvwxyz0123456789-', substring(name, i, 1))))

@description('Whether value is spelt as a GUID: 36 hexadecimal digits and hyphens.')
func isGuid(value string) bool =>
  length(value) == 36 && empty(filter(range(0, length(value)), i => !contains('0123456789abcdefABCDEF-', substring(value, i, 1))))

@description('Whether segments, a resource id split at each slash into nine parts, are a storage account\'s: /subscriptions/<id>/resourceGroups/<group>/providers/Microsoft.Storage/storageAccounts/<name>.')
func isStorageAccountPath(segments string[]) bool =>
  empty(segments[0]) && toLower(segments[1]) == 'subscriptions' && !empty(segments[2]) && toLower(segments[3]) == 'resourcegroups' && !empty(segments[4]) && toLower(segments[5]) == 'providers' && toLower(segments[6]) == 'microsoft.storage' && toLower(segments[7]) == 'storageaccounts' && !empty(segments[8])

@description('Whether name says it is a secret: ending in SECRET, PASSWORD or TOKEN, or naming an API key, in any case.')
func namesASecret(name string) bool =>
  endsWith(toUpper(name), 'SECRET') || endsWith(toUpper(name), 'PASSWORD') || endsWith(toUpper(name), 'TOKEN') || contains(toUpper(name), 'APIKEY') || contains(toUpper(name), 'API_KEY')

@description('Whether value, trimmed, is one whole \${env:...} or \${keyvault:...} reference.')
func isWholeReference(value string) bool =>
  ((take(trim(value), 6) == '\${env:' && length(trim(value)) > 7) || (take(trim(value), 11) == '\${keyvault:' && length(trim(value)) > 12)) && indexOf(trim(value), '}') == length(trim(value)) - 1

@description('The reference a run resolves a vault secret by, with the identity of the app or node that runs it.')
func vaultReference(vault string, secret string) string => '\${keyvault:${vault}/${secret}}'

// -------------------------------------------------------------------------------------------- where it goes ---

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Tags set on every resource this template creates.')
param tags { *: string } = {}

// ------------------------------------------------------------------------------------------------------ images ---

@description('The tag of this repository\'s images in the estate\'s registry: the commit they were built from. Each app\'s image is <registry>/osdu-delivery-<app>:<imageTag> unless its own image parameter names one. Every deployment sets each app\'s image to the one named here.')
param imageTag string = ''

@description('Container image reference for the control plane, e.g. myregistry.azurecr.io/osdu-delivery-control-plane:<tag>. Empty: <registry>/osdu-delivery-control-plane:<imageTag>.')
param controlPlaneImage string = ''

@description('Container image reference for the worker, e.g. myregistry.azurecr.io/osdu-delivery-worker:<tag>. Empty: <registry>/osdu-delivery-worker:<imageTag>.')
param workerImage string = ''

@description('Container image reference for the GUI, e.g. myregistry.azurecr.io/osdu-delivery-gui:<tag>. Empty: <registry>/osdu-delivery-gui:<imageTag>.')
param guiImage string = ''

@description('Deploy the MCP server (built from osdu/deploy/docker/mcp.Dockerfile), and with an AI Foundry model, the chat assistant on it. Its image is <registry>/osdu-delivery-mcp:<imageTag> unless mcpImage names one.')
param mcpEnabled bool = false

@description('Container image for the MCP server in HTTP mode, e.g. myregistry.azurecr.io/osdu-delivery-mcp:<tag>. Naming one deploys the MCP server whatever mcpEnabled says; empty with mcpEnabled off skips it, and with it the chat assistant.')
param mcpImage string = ''

@description('Name of a container registry in THIS resource group holding the images: each app identity is granted AcrPull on it. Leave empty for a public registry, or one you authorize yourself via acrLoginServer.')
param acrName string = ''

@description('Login server of a registry outside this resource group (grant AcrPull to the app identities yourself). Ignored when acrName is set.')
param acrLoginServer string = ''

// ----------------------------------------------------------------------------------------------------- network ---

@description('Address of an existing SQL Server or Azure SQL Managed Instance whose databases the estate uses, instead of creating a server here: host or host,port (a Managed Instance\'s private FQDN, its public endpoint with ,3342, or any reachable SQL Server). Set, nothing SQL is created: no server, firewall rule or database. The databases named by catalogDatabaseName and each of sqlConnections must already exist there, the network path is yours to provide (for a VNet-only Managed Instance, set infrastructureSubnetId so the apps reach its VNet), and an Entra administrator of the server runs the sqlSetupScript output to grant the apps their access. Only their connection secrets are written here. Empty creates an Azure SQL logical server and the databases.')
param existingSqlServer string = ''

@description('Resource id of a VNet subnet to integrate the Container Apps environment into: a workload-profiles environment needs a subnet of at least /27 delegated to Microsoft.App/environments. Empty deploys the environment without VNet integration.')
param infrastructureSubnetId string = ''

@description('The private ranges (CIDR, comma separated) the apps may reach: with infrastructureSubnetId set, the VNet ranges an OSDU, a storage account or a proxy behind a private endpoint resolves to. Empty reaches public addresses only.')
param privateNetworks string = ''

// --------------------------------------------------------------------------------------------------------- SQL ---

@description('How the apps sign in to SQL. entra (the default): each app as its own managed identity (Authentication=Active Directory Default), so no connection carries a credential; a server created here gets Entra-only authentication, administered by the estate\'s setup identity, which creates the apps\' database users inside every deployment (osdu/deploy/setup/Setup-Estate.ps1). With existingSqlServer, an Entra administrator of that server runs the sqlSetupScript output instead. password: the SQL login below, its password in each connection string in the vault.')
@allowed([
  'entra'
  'password'
])
param sqlAuthentication string = 'entra'

@description('Entra authentication: the people (or groups) given db_owner in every database of the estate, inside every deployment on a server created here (and in the sqlSetupScript output on an existing one): { name: as Entra shows it (a user\'s sign-in name, a group\'s display name), objectId, type: user or group }. The server\'s own administrator is the estate\'s setup identity, so a deployment needs no person\'s rights to grant the apps their access.')
param sqlAdmins sqlAdmin[] = []

@description('Password authentication only: the SQL login the apps sign in with, the admin of the server created here, or a login on the existing server or Managed Instance.')
param sqlAdminLogin string = 'osdudeliveryadmin'

@secure()
@description('Password authentication only: the password of sqlAdminLogin (Azure SQL enforces complexity). Single quotes and semicolons are handled; the value is quoted into the connection strings.')
param sqlAdminPassword string = ''

@description('Username of the initial admin user bootstrap provisioning creates on first start. Its password is generated into the vault by the deployment (osdu-delivery-admin-password) the first time, and read from there.')
param adminUsername string = 'admin'

// ----------------------------------------------------------------------------------------------------- sign-in ---

@description('Let this template create and own the Entra app registration users sign in with (App Roles + "Assignment required" baked in, so only assigned group members can sign in), through the Microsoft Graph Bicep extension. On (the default) enables "Sign in with Microsoft" and ignores azureAdAllowedTenantIds/azureAdClientId; the deploying principal then needs directory write (Application Administrator or Application.ReadWrite.All), and assigning a group needs Entra ID P1. Turn off for local-only sign-in, or to point at an externally managed registration via azureAdAllowedTenantIds + azureAdClientId.')
param provisionEntraApp bool = true

@description('Object id of the Entra security group whose members may sign in when provisionEntraApp is on. Only its members are assigned the app role and can obtain a token. Empty still enforces assignment required, so no one signs in until a group or users are assigned in the enterprise application. Reaches only this app\'s home tenant (subscription().tenantId); a tenant named in azureAdAdditionalAllowedTenantIds assigns its own users itself. Ignored when provisionEntraApp is off.')
param azureAdAllowedGroupObjectId string = ''

@description('Display name for the app registration this template creates when provisionEntraApp is on.')
param entraAppDisplayName string = 'OSDU Delivery'

@description('Stable unique name (Graph identity key) for that registration, so redeploys update the same app. Lowercase, no spaces.')
param entraAppUniqueName string = 'osdu-delivery'

@description('PROVISIONED app mode only (provisionEntraApp on): further Entra tenant (directory) ids, beyond this deployment\'s own home tenant (always trusted), whose users may also sign in. A non-empty list makes the app registration multi-tenant automatically; each named tenant\'s own admin must still consent to the app once and then assign the SqlFlow.User role to their own users/groups (this template has no directory access into a tenant it does not own); see the entraForeignTenantReminder output for that step. Empty keeps the app single-tenant.')
param azureAdAdditionalAllowedTenantIds string[] = []

@description('EXTERNAL app mode only (provisionEntraApp off): Microsoft Entra tenant (directory) ids allowed for GUI single sign-on, against an app registration you manage yourself. Set together with azureAdClientId to offer "Sign in with Microsoft"; leave empty for local sign-in only. Register the GUI origin (the guiUrl output) as a redirect URI on that SPA registration, and make it multi-tenant yourself if this list has more than one entry.')
param azureAdAllowedTenantIds string[] = []

@description('EXTERNAL app mode only (provisionEntraApp off): client id of the SPA app registration users sign in with. Set together with azureAdAllowedTenantIds to enable SSO. Ignored when provisionEntraApp is on (this template creates the registration and supplies the client id itself).')
param azureAdClientId string = ''

@description('Role a first-time SSO user is provisioned with (least privilege by default; an admin raises it afterwards in the GUI).')
param azureAdDefaultRole string = 'viewer'

// ------------------------------------------------------------------------------------------ flows and the fleet ---

@minLength(1)
@description('The OSDU partitions this deployment delivers to: at least one, since OSDU Delivery works against a partition and the control plane is not ready without one, and with several, exactly one marked default (the one a run that names none runs in). One object per partition: { id: the data-partition-id (letters, digits, underscore, hyphen and dot), default: bool (optional; one partition alone is the default without saying so), description: what it is for (optional), url, tokenUrl, scope, clientId (the values its runs resolve as OSDU_URL, OSDU_TOKEN_URL, OSDU_SCOPE and OSDU_CLIENT_ID), aclOwner, aclViewer, legalTag (optional: OSDU_ACL_OWNER, OSDU_ACL_VIEWER, OSDU_LEGAL_TAG), properties: { NAME: value } for any further name a flow spells (optional), clientSecretName (optional) }. The client secret is never given here: it is a secret a person puts in this estate\'s vault, named by clientSecretName (osdu-<id>-client-secret when left out, lower-cased, every character other than a-z and 0-9 a hyphen), and the partition reads it as OSDU_CLIENT_SECRET = \${keyvault:<vault>/<secret>}. A property whose name says it is a secret (ending in SECRET, PASSWORD or TOKEN, or naming an API key) takes such a reference, never the secret itself. The control plane registers the partitions when it starts (Osdu:Partitions, osdu/docs/reference/concepts/partitions.md), reports a client secret it cannot read yet as a failed start-up step, and only this configuration changes them.')
param osduPartitions osduPartition[]

@description('Further values set for no partition (Osdu:Properties:<NAME>), by the name a flow spells in \${env:NAME}: what every run of every kind resolves with, beside the SQL connections and the landing storage this template sets itself. A value is a setting or a \${keyvault:<vault>/<secret>} reference to a secret a person puts in the vault, never the secret itself.')
param properties { *: string } = {}

@description('The SQL databases the estate\'s flows read and write, by the name a flow spells in \${env:NAME} (OSDU_DATA_DB, OsduData by default, where the pre-ingestion flows land the files, the ingestion flows load the keyed tables and the delivery flows read them). Each is either a database on the estate\'s server ({ database, create: created here unless false, access: write lets the worker create, load and read its tables, read lets it read them; the control plane always reads }), its connection string built here with no credential under Entra authentication (each app signs in as its own identity) and written to the vault as osdu-delivery-conn-<name>, its users granted inside the deployment; or a database elsewhere ({ secretName: a vault secret a person puts there holding the whole connection string, its access yours to grant }). Every run is given each as \${keyvault:<vault>/<secret>}, which a node resolves with its own identity.')
param sqlConnections { *: sqlConnection } = {
  OSDU_DATA_DB: {
    database: 'OsduData'
  }
}

@description('SKU name of each database sqlConnections creates, e.g. S1 (ignored when existingSqlServer is set). They carry the record data, so they are sized apart from the metadata.')
param sqlConnectionSku string = 'S1'

@description('The flow repositories the control plane syncs, by name (ControlPlane:Bootstrap:Repositories): { remoteUrl (https), branch (main), syncIntervalSeconds (300, at least 30), credentialSecretName: for a private remote the vault secret a person puts its token in (a GitHub fine-grained token with read access to the repository\'s contents), credentialUsername where the host needs one, excludedFlowPaths }. The control plane registers them at start and keeps them as configured; nobody registers them by hand.')
param repositories { *: repository } = {}

@description('The GitHub App the estate\'s GitHub repositories are read with, in place of a token per repository (sqlflow/docs/reference/guides/github-app.md): { appId (its client ID), privateKeySecretName (the vault secret a person puts its .pem in), apiUrl (GitHub Enterprise only) }. A repository on the App\'s GitHub that names no credentialSecretName and leaves useGitHubApp on is read with a short-lived token the App mints for that repository alone, by the control plane and by each worker with its own identity; no token is stored anywhere. Left out, each repository is read with its own token, or with none.')
param githubApp gitHubAppOptions?

@description('The storage account the estate\'s flows read their source files from: created here ({ accountName }, derived from the resource group when left out) or adopted ({ existingAccountId }), with a container (landing). The worker\'s identity may read and write it and the control plane\'s read it, and every run is given its location as referenceName (LANDING_URL: abfss://<container>@<account>.dfs.<storage suffix>), which a flow names in its source location (\${env:LANDING_URL}/<folder>). Left out, the estate has none.')
param landingStorage landingStorageOptions?

@description('The pool the worker app serves. Empty takes untargeted runs only.')
param workerPool string = ''

@description('Upper bound for worker scale out.')
@minValue(1)
param workerMaxReplicas int = 10

@description('Minimum control plane replicas. Keep at 1 so the API is warm (a trigger never waits on a cold start).')
@minValue(1)
param controlPlaneMinReplicas int = 1

@description('Maximum control plane replicas. Keep at 1: the run queue is owned by exactly one replica (the dispatch lease), so an extra replica adds no dispatch capacity and refuses every node call that lands on it (503, retried by the node), which only slows hand-outs.')
@minValue(1)
param controlPlaneMaxReplicas int = 1

// ----------------------------------------------------------------------------- AI Foundry and the assistant ---

@description('Name for an Azure AI Foundry account (also its endpoint subdomain, globally unique) deployed alongside the estate, with the control plane and worker identities granted caller access. Empty skips AI Foundry, and with it the chat assistant.')
param aiFoundryName string = ''

@description('Name of the Foundry project created under the AI Foundry account.')
param aiFoundryProjectName string = 'osdu-delivery'

@description('Model deployed under the AI Foundry account for the chat assistant, e.g. gpt-5.1 (one that serves the Responses API, and that the region still accepts for new deployments: az cognitiveservices model list). Empty deploys no model, and leaves the assistant off.')
param aiFoundryModelName string = ''

@description('Version of that model. Empty lets the service pick the current default version.')
param aiFoundryModelVersion string = ''

@description('Capacity for the model deployment, in thousands of tokens per minute.')
@minValue(1)
param aiFoundryModelCapacity int = 30

@description('Optional audio-transcription model deployed beside the chat model (e.g. gpt-4o-mini-transcribe), enabling the GUI chat assistant\'s server-side voice input. Empty deploys none; the mic then falls back to the browser\'s built-in speech recognition.')
param aiFoundryTranscriptionModelName string = ''

@description('Version of the transcription model. Empty lets the service pick the current default version.')
param aiFoundryTranscriptionModelVersion string = ''

@description('Turn AI context on: questions starting with ! run read-only queries over the approved AI contexts, on the worker pool, with the connections those contexts open given to every run (sqlConnections, properties).')
param aiContextEnabled bool = false

// ------------------------------------------------------------------------------------------------------- names ---

@description('Name of the Container App running the control plane.')
param controlPlaneName string = 'osdu-delivery-control-plane'

@description('Name of the Container App running the worker pool.')
param workerName string = 'osdu-delivery-worker'

@description('Name of the Container App running the GUI.')
param guiName string = 'osdu-delivery-gui'

@description('Name of the Container App running the MCP server.')
param mcpName string = 'osdu-delivery-mcp'

@description('Name of the Container Apps managed environment.')
param environmentName string = 'osdu-delivery-env'

@description('Name of the Log Analytics workspace receiving container logs.')
param logAnalyticsName string = 'osdu-delivery-logs'

@minLength(3)
@maxLength(24)
@description('Key Vault name (globally unique, 3-24 alphanumerics and hyphens). Defaults to one derived from the resource group.')
param keyVaultName string = 'osdu-kv-${uniqueString(resourceGroup().id)}'

@description('Azure SQL logical server name (globally unique, lowercase). Defaults to one derived from the resource group.')
param sqlServerName string = 'osdu-sql-${uniqueString(resourceGroup().id)}'

@description('The metadata database: SQLFlow\'s catalog (pipelines, runs, schedules, lineage, sources, users) and, beside it, the module\'s `osdu` schema. Every run is given it as \${env:SQLFLOW_CATALOG_DB}, through which the flows read the osdu schema\'s views, and the flows name it in three-part names, so keep the name they use.')
param catalogDatabaseName string = 'OsduDelivery'

@description('Name of the estate\'s setup identity: the managed identity the deployment\'s own steps run as (osdu/deploy/setup/Setup-Estate.ps1), the Entra administrator of the SQL server created here and a Key Vault Secrets Officer of the estate\'s vault. No app runs as it.')
param setupIdentityName string = 'osdu-delivery-setup'

@description('SKU name of the metadata database, e.g. S1 (ignored when existingSqlServer is set). It carries the catalog, the run journal and the delivery ledger: modest, but polled continuously, so avoid serverless auto-pause.')
param sqlDatabaseSku string = 'S1'

@description('Changes on every deployment by default, so the setup steps run each time: a generated secret deleted from the vault is generated again, and the database grants are kept as this deployment says. Leave it to its default.')
param forceUpdateTag string = utcNow()

// ------------------------------------------------------------------------------------------------------ checks ---

// Checked first, so a deployment that cannot work stops before anything is written, saying which value is wrong.
// Each check fails only on a value that is wrong whatever else is set, and each checked value is what the rest of the
// template reads. A data-partition-id is unique whatever its case.
var osduPartitionIds = map(osduPartitions, p => toLower(p.id))
var osduPartitionDefaultCount = length(filter(osduPartitions, p => p.?default ?? false))
var osduPartitionsMisnamed = filter(osduPartitions, p => !isPartitionId(p.id))
var osduPartitionPropertiesMisnamed = flatten(map(osduPartitions, p => map(
  filter(objectKeys(p.?properties ?? {}), name => !isEnvironmentName(name)),
  name => '${p.id}: ${name}'
)))
var osduPartitionPropertiesHoldingSecrets = flatten(map(osduPartitions, p => map(
  filter(items(p.?properties ?? {}), property => namesASecret(property.key) && !isWholeReference(property.value)),
  property => '${p.id}: ${property.key}'
)))
var osduPartitionsChecked = !empty(osduPartitionsMisnamed)
  ? fail('A partition is named by its data-partition-id: letters, digits, underscore, hyphen and dot, at most 200 characters. Not so: ${join(map(osduPartitionsMisnamed, p => p.id), ', ')}.')
  : length(union(osduPartitionIds, [])) != length(osduPartitions)
      ? fail('osduPartitions names a partition twice: a partition is one data-partition-id, whatever its case.')
      : (osduPartitionDefaultCount > 1 || (osduPartitionDefaultCount == 0 && length(osduPartitions) > 1))
          ? fail('osduPartitions names ${length(osduPartitions)} partitions and ${osduPartitionDefaultCount} of them default: true. Mark exactly one, the partition a run that names none runs in.')
          : !empty(osduPartitionPropertiesMisnamed)
              ? fail('A property is named as a flow spells it in \${env:NAME}: a letter or underscore, then letters, digits and underscores, at most 64 characters. Not so: ${join(osduPartitionPropertiesMisnamed, ', ')}.')
              : !empty(osduPartitionPropertiesHoldingSecrets)
                  ? fail('A property whose name says it is a secret (ending in SECRET, PASSWORD or TOKEN, or naming an API key) takes a \${keyvault:<vault>/<secret>} reference, never the secret itself. Not so: ${join(osduPartitionPropertiesHoldingSecrets, ', ')}.')
                  : osduPartitions

var propertiesMisnamed = filter(objectKeys(properties), name => !isEnvironmentName(name))
var propertiesHoldingSecrets = map(filter(items(properties), property => namesASecret(property.key) && !isWholeReference(property.value)), property => property.key)
var propertiesChecked = !empty(propertiesMisnamed)
  ? fail('A property is named as a flow spells it in \${env:NAME}: a letter or underscore, then letters, digits and underscores, at most 64 characters. Not so: ${join(propertiesMisnamed, ', ')}.')
  : !empty(propertiesHoldingSecrets)
      ? fail('A property whose name says it is a secret (ending in SECRET, PASSWORD or TOKEN, or naming an API key) takes a \${keyvault:<vault>/<secret>} reference, never the secret itself. Not so: ${join(propertiesHoldingSecrets, ', ')}.')
      : properties

var sqlConnectionsMisnamed = filter(objectKeys(sqlConnections), name => !isEnvironmentName(name))
var sqlConnectionsNamedAsEstateDatabases = filter(objectKeys(sqlConnections), name => toUpper(name) == 'SQLFLOW_CATALOG_DB')
var sqlConnectionsNotOneOrTheOther = map(filter(items(sqlConnections), c => empty(c.value.?database ?? '') == empty(c.value.?secretName ?? '')), c => c.key)
var sqlConnectionsChecked = !empty(sqlConnectionsMisnamed)
  ? fail('A connection is named as a flow spells it in \${env:NAME}: a letter or underscore, then letters, digits and underscores, at most 64 characters. Not so: ${join(sqlConnectionsMisnamed, ', ')}.')
  : !empty(sqlConnectionsNamedAsEstateDatabases)
      ? fail('SQLFLOW_CATALOG_DB is the estate\'s own database, set by this template; name the connection otherwise. Not so: ${join(sqlConnectionsNamedAsEstateDatabases, ', ')}.')
      : !empty(sqlConnectionsNotOneOrTheOther)
          ? fail('Each SQL connection names exactly one of database (on the estate\'s server) and secretName (a vault secret holding the connection string of a database elsewhere). Not so: ${join(sqlConnectionsNotOneOrTheOther, ', ')}.')
          : sqlConnections

var sqlAdminsWithoutGuid = map(filter(sqlAdmins, a => !isGuid(a.objectId)), a => a.name)
var sqlAdminsChecked = !empty(sqlAdminsWithoutGuid)
  ? fail('Each of sqlAdmins has type user or group and an objectId that is a GUID. Not so: ${join(sqlAdminsWithoutGuid, ', ')}.')
  : sqlAdmins

var repositoriesMisnamed = filter(objectKeys(repositories), name => !isRepositoryName(name))
var repositoriesUnreachable = map(filter(items(repositories), r => take(r.value.remoteUrl, 8) != 'https://' || (r.value.?syncIntervalSeconds ?? 300) < 30), r => r.key)
var repositoriesChecked = !empty(repositoriesMisnamed)
  ? fail('A repository is named with letters, digits, underscore, hyphen and dot, starting with a letter or digit, at most 100 characters. Not so: ${join(repositoriesMisnamed, ', ')}.')
  : !empty(repositoriesUnreachable)
      ? fail('A repository\'s remoteUrl is an https:// URL, and it syncs at most every 30 seconds. Not so: ${join(repositoriesUnreachable, ', ')}.')
      : repositories

var githubAppIdValid = githubApp == null || (length(githubApp.?appId ?? '') >= 1 && length(githubApp.?appId ?? '') <= 100 && contains('ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789', take(githubApp.?appId ?? '', 1)) && empty(filter(range(0, length(githubApp.?appId ?? '')), i => !contains('ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_.-', substring(githubApp.?appId ?? '', i, 1)))))
var githubAppKeyValid = githubApp == null || (length(githubApp.?privateKeySecretName ?? '') >= 1 && length(githubApp.?privateKeySecretName ?? '') <= 127 && empty(filter(range(0, length(githubApp.?privateKeySecretName ?? '')), i => !contains('ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-', substring(githubApp.?privateKeySecretName ?? '', i, 1)))))
var githubAppApiValid = empty(githubApp.?apiUrl ?? '') || (take(githubApp.?apiUrl ?? '', 8) == 'https://' && length(githubApp.?apiUrl ?? '') > 8 && !contains(githubApp.?apiUrl ?? '', '?') && !contains(githubApp.?apiUrl ?? '', '#') && !contains(githubApp.?apiUrl ?? '', '@'))
var githubAppChecked = !githubAppIdValid
  ? fail('githubApp.appId is the App\'s client ID (like Iv23liAbCdEf) or its numeric App ID.')
  : !githubAppKeyValid
      ? fail('githubApp.privateKeySecretName is a Key Vault secret name: letters, digits and hyphens, at most 127 characters.')
      : !githubAppApiValid
          ? fail('githubApp.apiUrl is the https:// API root of GitHub Enterprise Server (https://<host>/api/v3) or GHE.com (https://api.<name>.ghe.com), with no user, query or fragment.')
          : githubApp

var landingStorageChecked = landingStorage == null
  ? null
  : !isContainerName(landingStorage.?container ?? 'landing')
      ? fail('landingStorage.container is a container name: 3 to 63 lowercase letters, digits and hyphens.')
      : !isEnvironmentName(landingStorage.?referenceName ?? 'LANDING_URL')
          ? fail('landingStorage.referenceName is named as a flow spells it in \${env:NAME}: a letter or underscore, then letters, digits and underscores, at most 64 characters.')
          : !empty(landingStorage.?existingAccountId ?? '') && (length(split(landingStorage.?existingAccountId ?? '', '/')) == 9 ? !isStorageAccountPath(split(landingStorage.?existingAccountId ?? '', '/')) : true)
              ? fail('landingStorage.existingAccountId is a storage account\'s resource id: /subscriptions/<id>/resourceGroups/<group>/providers/Microsoft.Storage/storageAccounts/<name>.')
              : landingStorage

// Under password authentication the SQL login signs in, so it needs its password; a server created here would be
// created without one.
var sqlAdminLoginChecked = sqlAuthentication == 'password' && empty(sqlAdminPassword)
  ? fail('sqlAdminPassword is required with sqlAuthentication password; set it, or use the default Entra authentication, where each app signs in as its own managed identity.')
  : sqlAdminLogin

// --------------------------------------------------------------------------------------------------- the estate ---

// Secret names shared with the per-tier templates (their defaults match these).
var catalogConnectionSecretName = 'osdu-delivery-catalog-db'
var jwtSigningKeySecretName = 'osdu-delivery-jwt-signing-key'
var adminPasswordSecretName = 'osdu-delivery-admin-password'
var nodeTokenSecretName = 'osdu-delivery-node-token'

// The deployment's own steps: one script for this template and the Terraform, run by the setup identity.
var setupScript = loadTextContent('../setup/Setup-Estate.ps1')

var createSql = empty(existingSqlServer)
var sqlEntraAuth = sqlAuthentication == 'entra'
var grantsHere = sqlEntraAuth && createSql

// The MCP server deploys on its own (any MCP client can use it). The GUI chat assistant rides on it and a Foundry
// model and needs nothing else, so it lights up once both exist; every chat run carries a short-lived token delegated
// from the signed-in user.
var mcpOn = mcpEnabled || !empty(mcpImage)
var aiFoundryEnabled = !empty(aiFoundryName)
var chatAssistantEnabled = mcpOn && aiFoundryEnabled && !empty(aiFoundryModelName)

// The Foundry project endpoint is deterministic from the account and project names, so the control plane never has to
// wait on (or cycle with) the ai-foundry module, which in turn grants the control plane's identity its role.
var aiFoundryProjectEndpoint = aiFoundryEnabled ? 'https://${aiFoundryName}.services.ai.azure.com/api/projects/${aiFoundryProjectName}' : ''

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource managedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    vnetConfiguration: empty(infrastructureSubnetId) ? null : {
      infrastructureSubnetId: infrastructureSubnetId
    }
    // The built-in serverless profile every workload-profiles environment has, which the apps run on.
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
  }
}

// ---------------------------------------------------------------------------------------------- setup identity ---

// The identity the deployment's own steps run as (osdu/deploy/setup/Setup-Estate.ps1): it generates the secrets nobody
// provides into the vault, and, as the Entra administrator of the SQL server created here, creates the apps' database
// users. No app runs as it.
resource setupIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: setupIdentityName
  location: location
  tags: tags
}

// The Key Vault Secrets Officer built-in role.
var keyVaultSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource setupSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, setupIdentity.id, keyVaultSecretsOfficerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficerRoleId)
    principalId: setupIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// The secrets the deployment generates, when the vault does not hold them yet; one it holds is never changed, so a
// value rotated by hand stays. Run before the apps, which read them when they are created, and at every deployment,
// so a secret deleted from the vault is generated again. Only the names travel: no generated value leaves the script.
resource setupSecrets 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: 'osdu-delivery-setup-secrets'
  location: location
  tags: tags
  kind: 'AzurePowerShell'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${setupIdentity.id}': {}
    }
  }
  properties: {
    azPowerShellVersion: '11.0'
    arguments: '-Step Secrets'
    scriptContent: setupScript
    environmentVariables: [
      {
        name: 'SETUP_SPEC'
        value: string({
          vault: keyVaultName
          generated: [
            { name: jwtSigningKeySecretName, kind: 'signing-key' }
            { name: adminPasswordSecretName, kind: 'password' }
            { name: nodeTokenSecretName, kind: 'node-token' }
          ]
          sql: null
        })
      }
    ]
    retentionInterval: 'P1D'
    cleanupPreference: 'OnSuccess'
    timeout: 'PT30M'
    forceUpdateTag: forceUpdateTag
  }
  dependsOn: [
    setupSecretsOfficer
  ]
}

// ---------------------------------------------------------------------------------------------- app identities ---

// The control plane's and the worker's identities, which their templates create too, with the same name, location and
// tags, so the second write changes nothing. They are created here first because the grants step and the landing
// storage's role assignments need them while the control plane starts: a module's outputs arrive only once its app is
// ready, and the control plane is not ready until its database user exists.
resource controlPlaneIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${controlPlaneName}-id'
  location: location
  tags: tags
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${workerName}-id'
  location: location
  tags: tags
}

// --------------------------------------------------------------------------------------------------------- SQL ---

// Under Entra authentication (the default) the server is administered by the estate's setup identity and has no SQL
// logins at all; under password authentication, the SQL admin login the connection strings carry.
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = if (createSql) {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    administratorLogin: sqlEntraAuth ? null : sqlAdminLoginChecked
    administratorLoginPassword: sqlEntraAuth ? null : sqlAdminPassword
    administrators: sqlEntraAuth
      ? {
          administratorType: 'ActiveDirectory'
          principalType: 'Application'
          login: setupIdentity.name
          // An application administers the server by its client id (az sql server create: "Object Id for User or Group,
          // Client Id for Applications"), the id its tokens carry.
          sid: setupIdentity.properties.clientId
          tenantId: subscription().tenantId
          azureADOnlyAuthentication: true
        }
      : null
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

// 0.0.0.0 is Azure's "allow Azure services" switch: the Container Apps consumption plan and the deployment scripts have
// no fixed outbound address for a narrower rule. To close the databases to the public internet, run the environment in
// a VNet and reach SQL over a private endpoint instead (then disable public access and drop this rule).
resource sqlAllowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (createSql) {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource catalogDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = if (createSql) {
  parent: sqlServer
  name: catalogDatabaseName
  location: location
  tags: tags
  sku: {
    name: sqlDatabaseSku
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}

// The SQL connections, each with the vault secret it is read from: built here for one on the estate's server, put there
// by hand for one elsewhere.
var sqlConnectionList = map(items(sqlConnectionsChecked), c => {
  name: c.key
  database: c.value.?database ?? ''
  create: c.value.?create ?? true
  access: c.value.?access ?? 'write'
  secretName: empty(c.value.?secretName ?? '')
    ? 'osdu-delivery-conn-${replace(toLower(c.key), '_', '-')}'
    : (c.value.?secretName ?? '')
})
var serverConnections = filter(sqlConnectionList, c => !empty(c.database))

// The databases this template creates for them: one per database name, whichever connections name it, and never the
// catalog's, which holds the osdu schema too.
var createdDatabases = createSql
  ? union(map(filter(serverConnections, c => c.create && c.database != catalogDatabaseName), c => c.database), [])
  : []

// The data databases the flows work in: the pre-ingestion flows land the source files there, the ingestion flows load
// the keyed tables, and the delivery flows read them (each in a schema of its own). Created only with the server; on an
// existing server they are provisioned out of band.
resource connectionDatabases 'Microsoft.Sql/servers/databases@2023-08-01-preview' = [for database in createdDatabases: if (createSql) {
  parent: sqlServer
  name: database
  location: location
  tags: tags
  sku: {
    name: sqlConnectionSku
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}]

// host,port for the connection strings: the created server on the standard port, or the existing address with its
// own port when it carries one (a Managed Instance public endpoint is host,3342).
var sqlServerAddress = createSql
  ? '${sqlServer!.properties.fullyQualifiedDomainName},1433'
  : (contains(existingSqlServer, ',') ? existingSqlServer : '${existingSqlServer},1433')

// The one place each connection string exists; everything else references the vault secret. With password
// authentication the SQL admin login signs in, its password quoted (embedded single quotes doubled) so any complex
// value survives ADO.NET parsing. With Entra authentication each app signs in as its own managed identity (the
// AZURE_CLIENT_ID it is given), so no connection carries a credential.
var quotedSqlAdminPassword = '\'${replace(sqlAdminPassword, '\'', '\'\'')}\''
var sqlCredentials = sqlEntraAuth
  ? 'Authentication=Active Directory Default'
  : 'User ID=${sqlAdminLoginChecked};Password=${quotedSqlAdminPassword}'
var connectionStringTail = '${sqlCredentials};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30'

resource catalogDbSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: catalogConnectionSecretName
  properties: {
    value: 'Server=tcp:${sqlServerAddress};Initial Catalog=${catalogDatabaseName};${connectionStringTail}'
  }
}

resource connectionSecrets 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = [for connection in serverConnections: {
  parent: keyVault
  name: connection.secretName
  properties: {
    value: 'Server=tcp:${sqlServerAddress};Initial Catalog=${connection.database};${connectionStringTail}'
  }
}]

// Under Entra authentication, the users each database needs for the app identities and the people sqlAdmins names, and
// the roles each holds. The control plane owns the catalog and the osdu schema (it migrates both) and reads every data
// database; the worker reaches the osdu schema only (and creates the dimension tables and views there), and creates,
// loads and reads the flows' tables in each data database it writes. The osdu schema is created first if missing, so
// the worker's grants hold before the control plane's first migration; the migration creates it only when it is
// missing. The estate's setup identity runs them inside every deployment (the grants step of Setup-Estate.ps1); with
// an existing server, an Entra administrator of that server runs the sqlSetupScript output instead. A user is E for an
// identity (by its client id) or a person, and X for a group (by its object id).
var controlPlaneUser = {
  name: controlPlaneIdentity.name
  id: controlPlaneIdentity.properties.clientId
  type: 'E'
}
var workerUser = {
  name: workerIdentity.name
  id: workerIdentity.properties.clientId
  type: 'E'
}
var adminUsers = map(sqlAdminsChecked, a => {
  name: a.name
  id: a.objectId
  type: (a.?type ?? 'user') == 'group' ? 'X' : 'E'
})

var adminsOwn = map(sqlAdminsChecked, a => 'ALTER ROLE db_owner ADD MEMBER [${replace(a.name, ']', ']]')}];')
var controlPlaneOwns = [
  'ALTER ROLE db_owner ADD MEMBER [${controlPlaneIdentity.name}];'
]
var workerReachesOsduSchema = [
  'IF SCHEMA_ID(N\'osdu\') IS NULL EXEC(N\'CREATE SCHEMA [osdu];\');'
  'GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE, ALTER, REFERENCES ON SCHEMA::osdu TO [${workerIdentity.name}];'
  'GRANT CREATE TABLE, CREATE VIEW TO [${workerIdentity.name}];'
]
var controlPlaneReads = [
  'ALTER ROLE db_datareader ADD MEMBER [${controlPlaneIdentity.name}];'
]
var workerWrites = [
  'ALTER ROLE db_ddladmin ADD MEMBER [${workerIdentity.name}];'
  'ALTER ROLE db_datareader ADD MEMBER [${workerIdentity.name}];'
  'ALTER ROLE db_datawriter ADD MEMBER [${workerIdentity.name}];'
]
var workerReads = [
  'ALTER ROLE db_datareader ADD MEMBER [${workerIdentity.name}];'
]

var sqlSetupEntries = concat(
  [
    {
      name: catalogDatabaseName
      purpose: 'the catalog, and the osdu schema beside it'
      users: concat([controlPlaneUser, workerUser], adminUsers)
      statements: concat(controlPlaneOwns, workerReachesOsduSchema, adminsOwn)
    }
  ],
  map(serverConnections, c => {
    name: c.database
    purpose: '${c.name}, the data the flows ${c.access == 'write' ? 'land, load and deliver from' : 'read'}'
    users: concat([controlPlaneUser, workerUser], adminUsers)
    statements: concat(controlPlaneReads, c.access == 'write' ? workerWrites : workerReads, adminsOwn)
  })
)

// One part per database, however many roles it plays, in name order: its purposes, users and statements each once,
// in the order they first appear.
var sqlSetupDatabases = map(items(groupBy(sqlSetupEntries, entry => entry.name)), database => {
  name: database.key
  purpose: join(reduce(database.value, [], (purposes, entry) => union(purposes, [entry.purpose])), '; ')
  users: reduce(database.value, [], (users, entry) => union(users, entry.users))
  statements: reduce(database.value, [], (statements, entry) => union(statements, entry.statements))
})

// For a server this template does not administer: the same users and roles, for its Entra administrator to run, as
// osdu/deploy/terraform/sql-setup.sql.tftpl renders them.
var sqlSetupScriptLines = concat(
  [
    '-- OSDU Delivery: the database users its apps (and the people sqlAdmins names) sign in as, under Entra authentication'
    '-- (each app as its own managed identity, no password anywhere), on a server this configuration does not administer.'
    '-- Run as an Entra administrator of ${existingSqlServer}, connected to each database below in turn (Azure SQL Database does not'
    '-- switch databases with USE); every statement can run again. Until they have run, the control plane reports its first'
    '-- start-up step as failed and retries it, and a worker cannot start.'
  ],
  flatten(map(sqlSetupDatabases, database => concat(
    [
      ''
      '-- ${database.name}: ${database.purpose}'
    ],
    map(database.users, user => 'IF USER_ID(N\'${replace(user.name, '\'', '\'\'')}\') IS NULL CREATE USER [${replace(user.name, ']', ']]')}] FROM EXTERNAL PROVIDER;'),
    database.statements
  )))
)
var sqlSetupScript = sqlEntraAuth && !createSql ? '${join(sqlSetupScriptLines, '\n')}\n' : ''

// The grants, inside every deployment, once the apps' identities exist: an app that starts before them retries its
// first connection until they hold.
resource setupGrants 'Microsoft.Resources/deploymentScripts@2023-08-01' = if (grantsHere) {
  name: 'osdu-delivery-setup-grants'
  location: location
  tags: tags
  kind: 'AzurePowerShell'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${setupIdentity.id}': {}
    }
  }
  properties: {
    azPowerShellVersion: '11.0'
    arguments: '-Step Grants'
    scriptContent: setupScript
    environmentVariables: [
      {
        name: 'SETUP_SPEC'
        value: string({
          vault: keyVaultName
          generated: []
          sql: {
            server: sqlServer!.properties.fullyQualifiedDomainName
            databases: map(sqlSetupDatabases, database => {
              name: database.name
              users: database.users
              statements: database.statements
            })
          }
        })
      }
    ]
    retentionInterval: 'P1D'
    cleanupPreference: 'OnSuccess'
    timeout: 'PT30M'
    forceUpdateTag: forceUpdateTag
  }
  dependsOn: [
    catalogDatabase
    connectionDatabases
    sqlAllowAzureServices
  ]
}

// -------------------------------------------------------------------------------------------------- partitions ---

// The OSDU partitions, as the control plane's configuration names them (Osdu:Partitions): it registers them when it
// starts, with the values their runs resolve. Each client secret is put in this estate's vault by hand and travels as a
// reference to it, which the control plane and the nodes resolve with their own identities when a run needs it.
//
// The vault secret each partition's client secret is read from: the one it names, or osdu-<id>-client-secret. A
// data-partition-id holds letters, digits, underscore, hyphen and dot, so lower-casing it and turning underscores and
// dots into hyphens leaves a vault secret name.
var osduPartitionSecretNames = toObject(osduPartitionsChecked, p => p.id, p => empty(p.?clientSecretName ?? '')
  ? 'osdu-${replace(replace(toLower(p.id), '_', '-'), '.', '-')}-client-secret'
  : (p.?clientSecretName ?? ''))

// The values a run bound to each partition resolves its flows' ${env:...} references with. The ones a partition names
// under properties come last, so a flow's own spelling of a value is the partition's to set.
var osduPartitionProperties = toObject(osduPartitionsChecked, p => p.id, p => union(
  {
    OSDU_URL: p.url
    OSDU_TOKEN_URL: p.tokenUrl
    OSDU_SCOPE: p.scope
    OSDU_CLIENT_ID: p.clientId
    OSDU_CLIENT_SECRET: vaultReference(keyVaultName, osduPartitionSecretNames[p.id])
  },
  empty(p.?aclOwner ?? '') ? {} : { OSDU_ACL_OWNER: p.?aclOwner ?? '' },
  empty(p.?aclViewer ?? '') ? {} : { OSDU_ACL_VIEWER: p.?aclViewer ?? '' },
  empty(p.?legalTag ?? '') ? {} : { OSDU_LEGAL_TAG: p.?legalTag ?? '' },
  p.?properties ?? {}
))

// The settings the control plane reads them from, one environment variable per value. One partition alone is the
// default without saying so.
var osduPartitionSettings = toObject(flatten(map(osduPartitionsChecked, p => concat(
  (p.?default ?? false) || length(osduPartitionsChecked) == 1 ? [{ name: 'Osdu__Partitions__${p.id}__Default', value: 'true' }] : [],
  empty(trim(p.?description ?? '')) ? [] : [{ name: 'Osdu__Partitions__${p.id}__Description', value: trim(p.?description ?? '') }],
  map(items(osduPartitionProperties[p.id]), property => { name: 'Osdu__Partitions__${p.id}__Properties__${property.key}', value: property.value })
))), setting => setting.name, setting => setting.value)

// --------------------------------------------------------------------------------------------- landing storage ---

// The storage account the flows read their source files from, created here or adopted: the worker's identity may read
// and write it (a flow that moves a file it has read), the control plane's read it (a person's preview of a flow).
var landingEnabled = landingStorageChecked != null
var landingAdoptedId = landingStorageChecked.?existingAccountId ?? ''
var landingCreated = landingEnabled && empty(landingAdoptedId)
var landingAdopted = landingEnabled && !empty(landingAdoptedId)
var landingContainer = landingStorageChecked.?container ?? 'landing'
var landingReferenceName = landingStorageChecked.?referenceName ?? 'LANDING_URL'
var landingAccountName = landingAdopted
  ? split(landingAdoptedId, '/')[8]
  : (empty(landingStorageChecked.?accountName ?? '') ? 'osdulanding${uniqueString(resourceGroup().id)}' : (landingStorageChecked.?accountName ?? ''))
var landingSubscriptionId = landingAdopted ? split(landingAdoptedId, '/')[2] : subscription().subscriptionId
var landingResourceGroupName = landingAdopted ? split(landingAdoptedId, '/')[4] : resourceGroup().name
var landingUrl = landingEnabled ? 'abfss://${landingContainer}@${landingAccountName}.dfs.${environment().suffixes.storage}' : ''

resource landingAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = if (landingCreated) {
  name: landingAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    isHnsEnabled: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    defaultToOAuthAuthentication: true
  }
}

resource landingBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = if (landingCreated) {
  parent: landingAccount
  name: 'default'
}

resource landingContainerResource 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (landingCreated) {
  parent: landingBlobService
  name: landingContainer
  properties: {
    publicAccess: 'None'
  }
}

// The apps' access to the account, granted in its own resource group, so an account adopted from another resource group
// or subscription is granted as one created here.
module landingAccess 'landing-access.bicep' = if (landingEnabled) {
  name: 'osdu-delivery-landing-access'
  scope: resourceGroup(landingSubscriptionId, landingResourceGroupName)
  params: {
    accountName: landingAccountName
    workerPrincipalId: workerIdentity.properties.principalId
    controlPlanePrincipalId: controlPlaneIdentity.properties.principalId
  }
  dependsOn: [
    landingContainerResource
  ]
}

// -------------------------------------------------------------------------------------------------------- apps ---

// Only read when acrName is set: the login server of the estate's registry.
resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = if (!empty(acrName)) {
  name: acrName
}

var registryLoginServer = empty(acrName) ? acrLoginServer : registry!.properties.loginServer

// Each app's image: its own parameter, or this repository's image of it at imageTag in the estate's registry, which an
// app whose image parameter is empty needs both of.
var imagesFromTag = empty(controlPlaneImage) || empty(workerImage) || empty(guiImage) || (mcpOn && empty(mcpImage))
var imageRegistry = imagesFromTag && (empty(imageTag) || empty(registryLoginServer))
  ? fail('An app\'s image is named neither by its own parameter nor by imageTag in a registry: set imageTag and acrName or acrLoginServer, or name every app\'s image.')
  : registryLoginServer
var controlPlaneImageReference = empty(controlPlaneImage) ? '${imageRegistry}/osdu-delivery-control-plane:${imageTag}' : controlPlaneImage
var workerImageReference = empty(workerImage) ? '${imageRegistry}/osdu-delivery-worker:${imageTag}' : workerImage
var guiImageReference = empty(guiImage) ? '${imageRegistry}/osdu-delivery-gui:${imageTag}' : guiImage
var mcpImageReference = empty(mcpImage) ? '${imageRegistry}/osdu-delivery-mcp:${imageTag}' : mcpImage

// Each app's FQDN is <app>.<environment default domain>, so the cross-origin wiring (GUI -> API base URL,
// control plane -> CORS origin, MCP server -> its own host) is computed up front instead of creating a dependency
// cycle between the apps.
var controlPlaneUrl = 'https://${controlPlaneName}.${managedEnvironment.properties.defaultDomain}'
var guiUrl = 'https://${guiName}.${managedEnvironment.properties.defaultDomain}'
var mcpFqdn = '${mcpName}.${managedEnvironment.properties.defaultDomain}'

// A non-empty azureAdAdditionalAllowedTenantIds means a second organization needs to sign in too, so the
// registration itself must be multi-tenant; otherwise it stays single-tenant (the secure-by-default case).
var entraSignInAudience = empty(azureAdAdditionalAllowedTenantIds) ? 'AzureADMyOrg' : 'AzureADMultipleOrgs'

// The values set for no partition every run of every kind resolves with (Osdu:Properties): the metadata database under
// the name the flows read the osdu schema's views by, each SQL connection, the landing storage, and the further
// properties. A connection is a vault reference a node resolves with its own identity.
var propertySettings = union(
  {
    Osdu__Properties__SQLFLOW_CATALOG_DB: vaultReference(keyVaultName, catalogConnectionSecretName)
  },
  toObject(sqlConnectionList, c => 'Osdu__Properties__${c.name}', c => vaultReference(keyVaultName, c.secretName)),
  landingEnabled ? { 'Osdu__Properties__${landingReferenceName}': landingUrl } : {},
  toObject(items(propertiesChecked), property => 'Osdu__Properties__${property.key}', property => property.value)
)

// The estate's GitHub App (githubApp) as the credential reference of a repository on its GitHub: a sync, and a worker
// checking out the commit a run is pinned to, each mint a token for that repository alone, reading the App's key from
// the vault with their own identity (sqlflow/docs/reference/guides/github-app.md).
var githubAppApiUrl = githubAppChecked.?apiUrl ?? ''
var githubAppApiRoot = empty(githubAppApiUrl) ? 'https://api.github.com' : (endsWith(githubAppApiUrl, '/') ? substring(githubAppApiUrl, 0, length(githubAppApiUrl) - 1) : githubAppApiUrl)
var githubAppApiHost = toLower(split(githubAppApiRoot, '/')[2])
var githubAppWebHost = startsWith(githubAppApiHost, 'api.') ? substring(githubAppApiHost, 4) : githubAppApiHost
var githubAppReference = githubAppChecked == null
  ? ''
  : '\${githubapp:app=${githubAppChecked.?appId ?? ''};key=keyvault:${keyVaultName}/${githubAppChecked.?privateKeySecretName ?? ''}${empty(githubAppApiUrl) ? '' : ';api=${githubAppApiRoot}'}}'

@description('The host of an https:// remote, lower-cased, without a user or a port.')
func remoteHost(remoteUrl string) string => toLower(first(split(last(split(split(remoteUrl, '/')[2], '@')), ':')))

// Each repository's credential: its own token when it names one, else the App when the repository is on the App's
// GitHub and leaves useGitHubApp on, else none (a public remote).
var repositoryCredentials = toObject(items(repositoriesChecked), r => r.key, r => !empty(r.value.?credentialSecretName ?? '')
  ? vaultReference(keyVaultName, r.value.?credentialSecretName ?? '')
  : (!empty(githubAppReference) && (r.value.?useGitHubApp ?? true) && (remoteHost(r.value.remoteUrl) == githubAppWebHost || remoteHost(r.value.remoteUrl) == 'www.${githubAppWebHost}'))
      ? githubAppReference
      : '')

// The repositories the control plane syncs, written at its start and kept as configured.
var repositorySettings = reduce(map(items(repositoriesChecked), r => union(
  {
    'ControlPlane__Bootstrap__Repositories__${r.key}__RemoteUrl': r.value.remoteUrl
    'ControlPlane__Bootstrap__Repositories__${r.key}__Branch': r.value.?branch ?? 'main'
    'ControlPlane__Bootstrap__Repositories__${r.key}__SyncIntervalSeconds': string(r.value.?syncIntervalSeconds ?? 300)
  },
  empty(repositoryCredentials[r.key])
    ? {}
    : { 'ControlPlane__Bootstrap__Repositories__${r.key}__CredentialReference': repositoryCredentials[r.key] },
  empty(r.value.?credentialUsername ?? '')
    ? {}
    : { 'ControlPlane__Bootstrap__Repositories__${r.key}__CredentialUsername': r.value.?credentialUsername ?? '' },
  toObject(
    range(0, length(r.value.?excludedFlowPaths ?? [])),
    i => 'ControlPlane__Bootstrap__Repositories__${r.key}__ExcludedFlowPaths__${i}',
    i => (r.value.?excludedFlowPaths ?? [])[i]
  )
)), {}, (settings, repository) => union(settings, repository))

var controlPlaneSettings = union(
  osduPartitionSettings,
  propertySettings,
  repositorySettings,
  // The node credential the workers present, which the control plane makes the admin's one node-scope token at start.
  {
    ControlPlane__Bootstrap__NodeTokenReference: vaultReference(keyVaultName, nodeTokenSecretName)
  }
)

// The Entra app users sign in with: created and owned here (App Roles + assignment required) when
// provisionEntraApp is on, otherwise an externally managed registration referenced by the azureAd* params.
// The GUI origin is the SPA redirect URI, known up front from the environment's default domain.
module entraApp 'entra-app.bicep' = if (provisionEntraApp) {
  name: 'osdu-delivery-entra-app'
  params: {
    displayName: entraAppDisplayName
    uniqueName: entraAppUniqueName
    redirectUri: guiUrl
    allowedGroupObjectId: azureAdAllowedGroupObjectId
    signInAudience: entraSignInAudience
  }
}

// This deployment's own subscription tenant is always trusted when the app is provisioned here; in external mode
// the full allow-list comes from the caller. Either way this is what the control plane validates tokens against,
// and an empty client id leaves SSO off (local sign-in only).
var effectiveAzureAdAllowedTenantIds = provisionEntraApp
  ? concat([subscription().tenantId], azureAdAdditionalAllowedTenantIds)
  : azureAdAllowedTenantIds
var effectiveAzureAdClientId = provisionEntraApp ? entraApp!.outputs.clientId : azureAdClientId

module controlPlane 'control-plane.bicep' = {
  name: 'osdu-delivery-control-plane-app'
  params: {
    location: location
    name: controlPlaneName
    managedEnvironmentId: managedEnvironment.id
    image: controlPlaneImageReference
    keyVaultName: keyVault.name
    catalogConnectionSecretName: catalogConnectionSecretName
    jwtSigningKeySecretName: jwtSigningKeySecretName
    // Compute belongs to the worker app; the API replica does API + scheduler + sync work only.
    workerEnabled: false
    // The GUI is a separate origin on Container Apps (one ingress FQDN per app).
    corsAllowedOrigins: [
      guiUrl
    ]
    bootstrapAdminUsername: adminUsername
    bootstrapAdminPasswordSecretName: adminPasswordSecretName
    azureAdAllowedTenantIds: effectiveAzureAdAllowedTenantIds
    azureAdClientId: effectiveAzureAdClientId
    azureAdDefaultRole: azureAdDefaultRole
    // The GUI chat assistant, on the estate's own Foundry account and MCP server (see chatAssistantEnabled).
    assistantEnabled: chatAssistantEnabled
    assistantFoundryProjectEndpoint: chatAssistantEnabled ? aiFoundryProjectEndpoint : ''
    assistantFoundryModelDeploymentName: chatAssistantEnabled ? aiFoundryModelName : ''
    assistantFoundryTranscriptionDeploymentName: chatAssistantEnabled ? aiFoundryTranscriptionModelName : ''
    assistantMcpServerUrl: chatAssistantEnabled ? mcp!.outputs.mcpUrl : ''
    aiContextEnabled: aiContextEnabled
    acrName: acrName
    acrLoginServer: acrLoginServer
    minReplicas: controlPlaneMinReplicas
    maxReplicas: controlPlaneMaxReplicas
    privateNetworks: privateNetworks
    // The estate's configuration: partitions, the values every run resolves with, repositories, the node credential.
    settings: controlPlaneSettings
    tags: tags
  }
  // The app reads the generated secrets and the connections at creation, and migrates the catalog and the OSDU module
  // at startup, retrying until the grants hold.
  dependsOn: [
    controlPlaneIdentity
    setupSecrets
    catalogDbSecret
    connectionSecrets
    catalogDatabase
    connectionDatabases
    sqlAllowAzureServices
  ]
}

// A node holds its identity and the catalog's connection alone, which it reads and writes the ledger in the osdu schema
// through: everything its runs read is handed to each run.
module worker 'worker.bicep' = {
  name: 'osdu-delivery-worker-app'
  params: {
    location: location
    name: workerName
    managedEnvironmentId: managedEnvironment.id
    image: workerImageReference
    keyVaultName: keyVault.name
    pool: workerPool
    controlPlaneUrl: controlPlaneUrl
    nodeTokenSecretName: nodeTokenSecretName
    privateNetworks: privateNetworks
    secretEnv: [
      {
        name: 'SQLFLOW_CATALOG_DB'
        secretName: catalogConnectionSecretName
      }
    ]
    acrName: acrName
    acrLoginServer: acrLoginServer
    maxReplicas: workerMaxReplicas
    tags: tags
  }
  dependsOn: [
    workerIdentity
    setupSecrets
    catalogDbSecret
    catalogDatabase
    sqlAllowAzureServices
  ]
}

module gui 'gui.bicep' = {
  name: 'osdu-delivery-gui-app'
  params: {
    location: location
    name: guiName
    managedEnvironmentId: managedEnvironment.id
    image: guiImageReference
    apiBaseUrl: controlPlaneUrl
    acrName: acrName
    acrLoginServer: acrLoginServer
    tags: tags
  }
}

// The MCP server over streamable HTTP: the chat assistant's tool source, and any other remote MCP client's that
// presents a SQLFlow bearer token. It answers only on its own host name.
module mcp 'mcp.bicep' = if (mcpOn) {
  name: 'osdu-delivery-mcp-app'
  params: {
    location: location
    name: mcpName
    managedEnvironmentId: managedEnvironment.id
    image: mcpImageReference
    controlPlaneUrl: controlPlaneUrl
    guiUrl: guiUrl
    allowedHosts: mcpFqdn
    acrName: acrName
    acrLoginServer: acrLoginServer
    tags: tags
  }
}

// Optional: an AI Foundry account + project beside the estate. The app identities are granted caller access
// (Cognitive Services User), so anything they run can call models keylessly; the control plane's identity is granted
// the OpenAI-inference role (Responses API + audio transcription) when the chat assistant is on.
module aiFoundry 'ai-foundry.bicep' = if (aiFoundryEnabled) {
  name: 'osdu-delivery-ai-foundry'
  params: {
    location: location
    name: aiFoundryName
    projectName: aiFoundryProjectName
    modelName: aiFoundryModelName
    modelVersion: aiFoundryModelVersion
    modelCapacity: aiFoundryModelCapacity
    transcriptionModelName: aiFoundryTranscriptionModelName
    transcriptionModelVersion: aiFoundryTranscriptionModelVersion
    userPrincipalIds: [
      controlPlane.outputs.identityPrincipalId
      worker.outputs.identityPrincipalId
    ]
    agentPrincipalIds: chatAssistantEnabled
      ? [
          controlPlane.outputs.identityPrincipalId
        ]
      : []
    tags: tags
  }
}

// ----------------------------------------------------------------------------------------------------- outputs ---

@description('Sign in here with the bootstrap admin: adminUsername, and the password the deployment generated into the vault (adminPasswordSecret).')
output guiUrl string = gui.outputs.guiUrl

// The name of the secret, never its value, which the template never sees: the linter goes by the output's name alone.
#disable-next-line outputs-should-not-contain-secrets
@description('The name of the vault secret holding the initial admin\'s password, generated by the deployment the first time: az keyvault secret show --vault-name <vault> --name <this> --query value -o tsv. Never the password.')
output adminPasswordSecret string = adminPasswordSecretName

@description('Client id of the Entra registration users sign in with: created and owned by this template when provisionEntraApp is on, otherwise the azureAdClientId you passed. Empty means SSO is off (local sign-in only).')
output entraClientId string = effectiveAzureAdClientId

@description('The GUI origin registered as the SPA redirect URI. When provisionEntraApp is on this template already set it on the registration; in external mode add it yourself (Single-page application platform). Empty when SSO is off.')
output entraRedirectUri string = empty(effectiveAzureAdClientId) ? '' : gui.outputs.guiUrl

@description('Next step for Bicep-owned SSO: assignment is required, so no one can sign in until members are assigned. Set azureAdAllowedGroupObjectId to a security group to grant its members access, or assign users/groups in the enterprise application. Empty when a group was already assigned or SSO is off.')
output entraAssignmentReminder string = (provisionEntraApp && empty(azureAdAllowedGroupObjectId)) ? 'Assign a group via azureAdAllowedGroupObjectId (or in the enterprise application) so members can sign in; assignment is required and none are assigned yet.' : ''

@description('Next step for each tenant named in azureAdAdditionalAllowedTenantIds: this template cannot grant role assignments outside its own home tenant, so an admin in that tenant must consent to the app (creates their own local enterprise application object) and then assign the SqlFlow.User role to their own users or groups on it. Empty when no additional tenant was configured or SSO is off.')
output entraForeignTenantReminder string = (provisionEntraApp && !empty(azureAdAdditionalAllowedTenantIds))
  ? 'For each tenant in azureAdAdditionalAllowedTenantIds: an admin there must consent to app ${effectiveAzureAdClientId}, then assign the SqlFlow.User role to their own users/groups in their own enterprise application view.'
  : ''

@description('The API base URL: point CLI remotes (SQLFLOW_URL) and any external scheduler at this.')
output controlPlaneBaseUrl string = controlPlane.outputs.controlPlaneBaseUrl

@description('Client id of the control plane identity, for granting access to flow secrets beyond this vault.')
output controlPlaneIdentityClientId string = controlPlane.outputs.identityClientId

@description('Client id of the node identity, for granting access to the payload storage and the OSDU credentials its pool\'s flows use.')
output workerIdentityClientId string = worker.outputs.identityClientId

@description('Name of the control plane identity: the database user it signs in as (db_owner of the metadata database, reader of each data database), granted inside every deployment on a server created here.')
output controlPlaneIdentityName string = controlPlane.outputs.identityName

@description('Name of the node identity: the database user it signs in as (the osdu schema only, and each data database as its sqlConnections access says), granted inside every deployment on a server created here.')
output workerIdentityName string = worker.outputs.identityName

@description('Name of the estate\'s setup identity: the SQL server\'s Entra administrator and the vault\'s secrets officer, which the deployment\'s own steps run as. No app runs as it.')
output setupIdentityName string = setupIdentity.name

@description('How the apps sign in to SQL: entra (each app as its own managed identity) or password.')
output sqlAuthentication string = sqlAuthentication

@description('With existingSqlServer under Entra authentication, the T-SQL an Entra administrator of that server runs, database by database, to create the apps\' users; every statement can run again. Empty otherwise: on a server created here the deployment grants them itself.')
output sqlSetupScript string = sqlSetupScript

@description('SQL server address (the connection strings are in Key Vault: osdu-delivery-catalog-db, which holds the osdu schema too, and one per SQL connection).')
output sqlServerFqdn string = createSql ? sqlServer!.properties.fullyQualifiedDomainName : existingSqlServer

@description('The SQL connections every run is given, by the name a flow spells: the vault secret each is read from.')
output sqlConnections { *: string } = toObject(sqlConnectionList, c => c.name, c => c.secretName)

@description('The landing storage every run is given (landingStorage.referenceName), or empty without one.')
output landingUrl string = landingUrl

@description('The vault holding every estate secret: the generated ones, and the ones put here by hand (each partition\'s client secret, a repository\'s token or the GitHub App\'s private key, a connection elsewhere).')
output keyVaultName string = keyVault.name

@description('The vault secret the estate\'s GitHub App\'s private key is read from, put there by hand: az keyvault secret set --vault-name <keyVaultName> --name <this> --file <the App\'s .pem>. Empty when the estate names no App.')
output githubAppPrivateKeySecret string = githubAppChecked.?privateKeySecretName ?? ''

@description('The vault\'s URI.')
output keyVaultUri string = keyVault.properties.vaultUri

@description('The AI Foundry endpoint, or empty when aiFoundryName was not set.')
output aiFoundryEndpoint string = aiFoundryEnabled ? aiFoundry!.outputs.endpoint : ''

@description('The Foundry project endpoint the chat assistant connects to, or empty when aiFoundryName was not set.')
output aiFoundryProjectEndpoint string = aiFoundryProjectEndpoint

@description('The MCP endpoint URL (register it in an editor or any remote MCP client with a SQLFlow token), or empty when the MCP server is not deployed.')
output mcpUrl string = mcpOn ? mcp!.outputs.mcpUrl : ''

@description('Whether the GUI chat assistant is on: the MCP server and a Foundry model are both deployed.')
output chatAssistantEnabled bool = chatAssistantEnabled

@description('The OSDU partitions the control plane registers when it starts: for each, whether it is the default and the vault secret its client secret is read from, which a person puts in the vault (the control plane reports one it cannot read yet as a failed start-up step).')
output osduPartitions { *: osduPartitionReport } = toObject(osduPartitionsChecked, p => p.id, p => {
  default: (p.?default ?? false) || length(osduPartitionsChecked) == 1
  clientSecretName: osduPartitionSecretNames[p.id]
})
