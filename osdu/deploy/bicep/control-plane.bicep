// Deploys the OSDU Delivery control plane as an always-on Azure Container App: the warm API the GUI, the CLI and
// any external scheduler call. It runs with a user-assigned managed identity that reads its secrets (the catalog
// connection, the JWT signing key and the initial admin's password) from Key Vault, and that SAME identity is what the
// engine uses to resolve ${keyvault:...} references at run time (SQLFLOW_AZURE_AUTH=mi), so no secret value is ever
// placed in this template or in app configuration. Provide an existing Container Apps environment (with the
// Consumption workload profile) and a Key Vault holding those secrets (main.bicep's setup step generates them);
// everything else is created here.
//
//   az deployment group create -g <rg> -f control-plane.bicep \
//     -p managedEnvironmentId=<env-id> image=<registry>/osdu-delivery-control-plane:<tag> keyVaultName=<kv> \
//        settings=@settings.json
//
// where settings.json is the estate's configuration (see THE CONFIGURATION below), naming at least one OSDU
// partition, e.g.
// { "Osdu__Partitions__dev__Properties__OSDU_URL": "https://<platform host>",
//   "Osdu__Partitions__dev__Properties__OSDU_CLIENT_SECRET": "${keyvault:<kv>/osdu-dev-client-secret}", ... }.
//
// The OSDU module's `osdu` schema lives in the catalog database by default; startup applies SQLFlow's catalog
// migrations and then the module's.
//
// A FLOW'S READS RUN HERE. What a person asks of a flow and waits on (a probe of its target, a record read back from
// OSDU, its rows read from the ingestion tables, a preview, a scope's values, the explorer) runs in this app under the
// flow's own credentials, resolving with the same values the control plane hands every run (its settings, the estate's
// configuration), with this app's identity reading the vault. The control plane never delivers; runs, value checks and
// removals stay on the nodes.
//
// THE CONFIGURATION is this app's settings, one environment variable per value, which the control plane applies at
// every start (its start-up pass, GET /api/v1/bootstrap) and hands to every run: the OSDU partitions
// (Osdu__Partitions__<id>__Default, __Description and __Properties__<NAME>), the values set for no partition
// (Osdu__Properties__<NAME>: the SQL connections, the landing storage, any further property), the repositories it syncs
// (ControlPlane__Bootstrap__Repositories__<name>__...) and where the node credential the workers present is
// (ControlPlane__Bootstrap__NodeTokenReference). It is not ready without a partition to work against, so a deployment
// names at least one. A secret among the values is a ${keyvault:<vault>/<secret>} reference, never the secret: a person
// puts each partition's client secret and each private repository's token in the vault, and the control plane reports
// one it cannot read as a failed start-up step.
//
// The defaults deploy the single-app mode (in-process worker enabled, no CORS). main.bicep composes this module
// with worker.bicep and gui.bicep into the full estate: there the worker is disabled (the API replica does API
// work only) and the GUI origin is CORS-listed.

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Name of the Container App (and the prefix for its managed identity).')
param name string = 'osdu-delivery-control-plane'

@description('Resource id of an existing Container Apps managed environment to host the app. The app runs on its Consumption workload profile.')
param managedEnvironmentId string

@description('Container image reference, e.g. myregistry.azurecr.io/osdu-delivery-control-plane:<tag>')
param image string

@description('Name of an existing Key Vault holding the catalog connection, the JWT signing key and the initial admin\'s password; the app identity is granted Key Vault Secrets User on it.')
param keyVaultName string

@description('Key Vault secret name for the catalog ADO.NET connection string, which the delivery module\'s `osdu` schema is reached through too.')
param catalogConnectionSecretName string = 'osdu-delivery-catalog-db'

@description('Key Vault secret name for the JWT signing key (at least 32 bytes): the one the deployment generates into the vault.')
param jwtSigningKeySecretName string = 'osdu-delivery-jwt-signing-key'

@description('JWT issuer the control plane validates.')
param jwtIssuer string = 'sqlflow-control-plane'

@description('JWT audience the control plane validates.')
param jwtAudience string = 'sqlflow'

@description('Run the in-process worker inside the control plane (single-app mode). Set false when dedicated nodes (worker.bicep) take the work, so the API replica does API work only.')
param workerEnabled bool = true

@description('Browser origins allowed to call the API, e.g. the gui.bicep app URL. Leave empty when the GUI is same-origin behind a path-splitting front, or when only non-browser clients call the API.')
param corsAllowedOrigins string[] = []

@description('Key Vault secret name for the initial admin\'s password: the one the deployment generates into the vault. When set, bootstrap provisioning creates that admin on first start; leave empty to skip user bootstrap.')
param bootstrapAdminPasswordSecretName string = ''

@description('Username for the bootstrap admin. Only used when bootstrapAdminPasswordSecretName is set.')
param bootstrapAdminUsername string = 'admin'

@description('The estate\'s configuration, by environment variable name: the OSDU partitions the control plane registers when it starts (Osdu__Partitions__<id>__...), the values every run resolves with (Osdu__Properties__<NAME>), the repositories it syncs (ControlPlane__Bootstrap__Repositories__<name>__...) and the node credential\'s vault reference (ControlPlane__Bootstrap__NodeTokenReference). A secret never goes here; a setting that needs one holds a \${keyvault:<vault>/<secret>} reference.')
param settings { *: string } = {}

@description('The private ranges (CIDR, comma separated) the reads of a flow may reach, for an environment integrated into a VNet whose OSDU or ingestion database resolves to private addresses (SQLFLOW_DELIVERY_PRIVATE_NETWORKS). The same value the worker app takes. Empty reaches public addresses only; loopback, link-local and cloud metadata addresses are never reachable.')
param privateNetworks string = ''

@description('CIDRs of the ingress hops to trust for X-Forwarded-* headers. Leave empty to keep proxy trust off; per-client rate limiting then keys on the ingress hop address instead of the real client.')
param proxyKnownNetworks string[] = []

@description('Microsoft Entra tenant (directory) ids allowed to sign in to the GUI. Set together with azureAdClientId to offer "Sign in with Microsoft" on the login page; leave empty for local username/password only. Multiple tenants are supported: the app registration itself must be multi-tenant, and each additional tenant\'s admin must consent to it.')
param azureAdAllowedTenantIds string[] = []

@description('Client id of the SPA app registration users sign in with (its redirect URI must be the GUI origin). Set together with azureAdAllowedTenantIds to enable SSO.')
param azureAdClientId string = ''

@description('Role a first-time SSO user is provisioned with (least privilege by default; an admin raises it afterwards).')
param azureAdDefaultRole string = 'viewer'

@description('Enable the GUI chat assistant (/api/v1/chat), with every agent run presenting a short-lived token delegated from the calling user to the MCP server. Requires the Foundry and MCP parameters below; the app identity signs in to the Foundry account (no key), so grant it Cognitive Services OpenAI User there (ai-foundry.bicep agentPrincipalIds).')
param assistantEnabled bool = false

@description('The Foundry project endpoint the assistant runs against (https://<account>.services.ai.azure.com/api/projects/<project>). Required when assistantEnabled.')
param assistantFoundryProjectEndpoint string = ''

@description('The model deployment the assistant runs on. Required when assistantEnabled.')
param assistantFoundryModelDeploymentName string = ''

@description('Optional audio-transcription deployment in the same account; empty leaves voice input on the browser\'s built-in speech recognition.')
param assistantFoundryTranscriptionDeploymentName string = ''

@description('The deployed MCP server endpoint (https://<mcp host>/mcp, the mcpUrl output of mcp.bicep), the assistant\'s tool source. Required when assistantEnabled.')
param assistantMcpServerUrl string = ''

@description('Turn AI context on (ControlPlane:AiContext:Enabled): questions starting with ! run read-only queries over the approved AI contexts, on the nodes.')
param aiContextEnabled bool = false

@description('Name of a container registry in THIS resource group: the template grants the app identity AcrPull on it and configures the pull. Leave empty for a public registry, or one you authorize yourself via acrLoginServer.')
param acrName string = ''

@description('Login server of a registry outside this resource group (grant AcrPull to the app identity yourself). Ignored when acrName is set.')
param acrLoginServer string = ''

@description('Minimum replicas. Keep at 1 so the API is warm (a trigger never waits on a cold start).')
@minValue(1)
param minReplicas int = 1

@description('Maximum replicas. Keep at 1: the run queue is owned by exactly one replica (the dispatch lease), so an extra replica adds no dispatch capacity and refuses every node call that lands on it (503, retried by the node), which only slows hand-outs.')
@minValue(1)
param maxReplicas int = 1

@description('Tags set on the app and its identity.')
param tags { *: string } = {}

// Checked first, so a deployment that cannot work stops before anything is created, saying why.
var mcpServerUrlChecked = empty(assistantMcpServerUrl) || take(assistantMcpServerUrl, 8) == 'https://'
  ? assistantMcpServerUrl
  : fail('assistantMcpServerUrl must be an https:// URL.')

// The Key Vault Secrets User built-in role, so the app's identity can read the configured secrets.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
// The AcrPull built-in role, for managed-identity image pull from a same-group registry.
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${name}-id'
  location: location
  tags: tags
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource keyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, identity.id, keyVaultSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Only referenced when acrName is set; the placeholder name is never resolved (ARM evaluates the branch lazily).
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: empty(acrName) ? 'unused' : acrName
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(acrName)) {
  scope: acr
  name: guid(resourceGroup().id, acrName, identity.id, acrPullRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

var vaultUri = 'https://${keyVaultName}${environment().suffixes.keyvaultDns}/'
var registryServer = !empty(acrName) ? acr.properties.loginServer : acrLoginServer

// Secrets are pulled from Key Vault by the app's managed identity; their values never appear here.
var baseSecrets = [
  {
    name: 'catalog-db'
    keyVaultUrl: '${vaultUri}secrets/${catalogConnectionSecretName}'
    identity: identity.id
  }
  {
    name: 'jwt-signing-key'
    keyVaultUrl: '${vaultUri}secrets/${jwtSigningKeySecretName}'
    identity: identity.id
  }
]

var bootstrapSecrets = empty(bootstrapAdminPasswordSecretName) ? [] : [
  {
    name: 'bootstrap-admin-password'
    keyVaultUrl: '${vaultUri}secrets/${bootstrapAdminPasswordSecretName}'
    identity: identity.id
  }
]

var baseEnv = [
  // The catalog connection, which holds the delivery module's `osdu` schema too; the control plane's default
  // ConnectionReference (${env:SQLFLOW_CATALOG_DB}) reads exactly this variable, and startup migrates and verifies the
  // catalog and then the module.
  {
    name: 'SQLFLOW_CATALOG_DB'
    secretRef: 'catalog-db'
  }
  // Resolve ${keyvault:...}/cloud-storage credentials at run time via this managed identity.
  {
    name: 'SQLFLOW_AZURE_AUTH'
    value: 'mi'
  }
  {
    name: 'AZURE_CLIENT_ID'
    value: identity.properties.clientId
  }
  {
    name: 'ControlPlane__Jwt__SigningKey'
    secretRef: 'jwt-signing-key'
  }
  {
    name: 'ControlPlane__Jwt__Issuer'
    value: jwtIssuer
  }
  {
    name: 'ControlPlane__Jwt__Audience'
    value: jwtAudience
  }
  {
    name: 'ControlPlane__Worker__Enabled'
    value: workerEnabled ? 'true' : 'false'
  }
]

var bootstrapEnv = empty(bootstrapAdminPasswordSecretName) ? [] : [
  {
    name: 'ControlPlane__Bootstrap__AdminUsername'
    value: bootstrapAdminUsername
  }
  {
    name: 'ControlPlane__Bootstrap__AdminPasswordReference'
    secretRef: 'bootstrap-admin-password'
  }
]

var privateNetworksEnv = empty(privateNetworks) ? [] : [
  {
    name: 'SQLFLOW_DELIVERY_PRIVATE_NETWORKS'
    value: privateNetworks
  }
]

var corsEnv = [for (origin, i) in corsAllowedOrigins: {
  name: 'ControlPlane__Cors__AllowedOrigins__${i}'
  value: origin
}]

var proxyNetworkEnv = [for (cidr, i) in proxyKnownNetworks: {
  name: 'ControlPlane__Proxy__KnownNetworks__${i}'
  value: cidr
}]

// Proxy trust is all-or-nothing: enabling it while trusting no networks fails startup validation, so the enable
// switch rides on the network list being non-empty.
var proxyEnv = empty(proxyKnownNetworks) ? [] : concat([
  {
    name: 'ControlPlane__Proxy__Enabled'
    value: 'true'
  }
], proxyNetworkEnv)

// Entra SSO auto-enables in the control plane once at least one allowed tenant id and a client id are present, so
// supplying both is the whole switch; either one missing leaves the login page offering local username/password
// only. Each allowed tenant becomes its own indexed env var, since the control plane binds AllowedTenantIds as a
// list.
var azureAdTenantEnv = [for (tenantId, i) in azureAdAllowedTenantIds: {
  name: 'ControlPlane__AzureAd__AllowedTenantIds__${i}'
  value: tenantId
}]

var entraEnv = (empty(azureAdAllowedTenantIds) || empty(azureAdClientId)) ? [] : concat(azureAdTenantEnv, [
  {
    name: 'ControlPlane__AzureAd__ClientId'
    value: azureAdClientId
  }
  {
    name: 'ControlPlane__AzureAd__DefaultRole'
    value: azureAdDefaultRole
  }
])

// The GUI chat assistant (Foundry mode: the identity above signs into the account, no API key). The chat endpoints
// stay mapped when disabled and report the switch via /chat/capabilities, so this block is the whole difference
// between an estate with chat and one without.
var assistantEnv = !assistantEnabled ? [] : concat([
  {
    name: 'ControlPlane__Assistant__Enabled'
    value: 'true'
  }
  {
    name: 'ControlPlane__Assistant__Foundry__ProjectEndpoint'
    value: assistantFoundryProjectEndpoint
  }
  {
    name: 'ControlPlane__Assistant__Foundry__ModelDeploymentName'
    value: assistantFoundryModelDeploymentName
  }
  {
    name: 'ControlPlane__Assistant__Mcp__ServerUrl'
    value: mcpServerUrlChecked
  }
], empty(assistantFoundryTranscriptionDeploymentName) ? [] : [
  {
    name: 'ControlPlane__Assistant__Foundry__TranscriptionDeploymentName'
    value: assistantFoundryTranscriptionDeploymentName
  }
])

var aiContextEnv = !aiContextEnabled ? [] : [
  {
    name: 'ControlPlane__AiContext__Enabled'
    value: 'true'
  }
]

// The estate's configuration, in name order.
var settingsEnv = [for setting in items(settings): {
  name: setting.key
  value: setting.value
}]

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: managedEnvironmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: empty(registryServer) ? [] : [
        {
          server: registryServer
          identity: identity.id
        }
      ]
      secrets: concat(baseSecrets, bootstrapSecrets)
    }
    template: {
      containers: [
        {
          name: 'control-plane'
          image: image
          resources: {
            cpu: json('0.5')
            memory: '1.0Gi'
          }
          env: concat(baseEnv, bootstrapEnv, corsEnv, proxyEnv, entraEnv, assistantEnv, aiContextEnv, privateNetworksEnv, settingsEnv)
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
  // The app resolves its Key Vault secrets (and pulls the image) at creation, so the role grants must exist
  // first. ARM drops the acrPull entry when that resource's condition is false.
  dependsOn: [
    keyVaultAccess
    acrPull
  ]
}

@description('The control plane base URL: point CLI remotes (SQLFLOW_URL), the nodes and any external scheduler at this.')
output controlPlaneBaseUrl string = 'https://${app.properties.configuration.ingress.fqdn}'

@description('The client id of the app identity (grant it access to flow secrets beyond this estate\'s vault).')
output identityClientId string = identity.properties.clientId

@description('The principal (object) id of the app identity, for role assignments made outside this template.')
output identityPrincipalId string = identity.properties.principalId

@description('Name of the app identity: the user to create in each database it uses under Entra authentication (CREATE USER [<name>] FROM EXTERNAL PROVIDER).')
output identityName string = identity.name
