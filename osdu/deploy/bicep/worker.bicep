// Deploys one OSDU Delivery worker pool as a Container App: a compute node that polls the control plane's
// dispatcher for work. Nodes expose nothing (no ingress; outbound HTTPS to the control plane, outbound SQL to the
// data databases their flows read, outbound HTTPS to the OSDU endpoint, and outbound git for a pinned run
// without a snapshot) and scale 0..N on the control plane's own replica target through the built-in KEDA
// metrics-api scaler, so an idle pool costs nothing. A cold-started replica needs only its environment: it polls,
// is handed a run with its definition and the values it resolves with, fetches the snapshotted YAML (or
// materializes the pinned commit), executes, streams the trace, and reports back.
//
// THE OSDU LEDGER ON A NODE. The delivery engine reads and writes the ledger in the `osdu` schema per record
// while it plans and delivers. The schema lives in the catalog's database, and SQLFlow itself gives a node no catalog
// connection, so the node is given the catalog's: SQLFLOW_CATALOG_DB in secretEnv, pointing at the Key Vault secret
// holding the connection string (osdu/docs/reference/concepts/environment-variables.md). Under Entra authentication it
// carries no credential, and the node's identity holds grants on the `osdu` schema alone there. Without it the node
// refuses to start.
// Everything a run reads beside it (the data databases, the landing storage, the OSDU platform and its client secret)
// is handed to each run by the control plane, from the estate's configuration, as values and ${keyvault:...}
// references this app's identity resolves; so a node carries nothing else.
//
// This is the Container Apps mirror of osdu/deploy/k8s/worker-pool.yaml: deploy one copy per pool (set name and
// pool together). Secrets come from an existing Key Vault, read by the app's user-assigned managed identity;
// that same identity resolves ${keyvault:...} references at run time (SQLFLOW_AZURE_AUTH=mi).
//
//   az deployment group create -g <rg> -f worker.bicep \
//     -p managedEnvironmentId=<env-id> image=<registry>/osdu-delivery-worker:<tag> keyVaultName=<kv> \
//        controlPlaneUrl=https://<control-plane-fqdn> nodeTokenSecretName=osdu-delivery-node-token \
//        secretEnv='[{"name": "SQLFLOW_CATALOG_DB", "secretName": "osdu-delivery-catalog-db"}]'

@description('A variable a node reads from the vault.')
type secretEnvironmentVariable = {
  @description('The environment variable.')
  name: string

  @description('The Key Vault secret holding its value.')
  secretName: string
}

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Name of the Container App (and the prefix for its managed identity). Deploy one app per pool, e.g. osdu-delivery-worker-onprem.')
param name string = 'osdu-delivery-worker'

@description('Resource id of an existing Container Apps managed environment to host the app. The app runs on its Consumption workload profile.')
param managedEnvironmentId string

@description('Container image reference, e.g. myregistry.azurecr.io/osdu-delivery-worker:<tag>')
param image string

@description('Name of an existing Key Vault holding the node token and the secrets secretEnv names; the app identity is granted Key Vault Secrets User on it.')
param keyVaultName string

@description('The control plane base URL the node polls for work over the node protocol (https://<control-plane-fqdn>). Every call is outbound from the node, and the scale rule reads the pool\'s replica target from the same host.')
param controlPlaneUrl string

@description('Key Vault secret name holding the node credential the node presents to the control plane, and the scale rule to the scale-target endpoint: the token the deployment generates into the vault (osdu-delivery-node-token) and the control plane accepts (ControlPlane:Bootstrap:NodeTokenReference).')
@minLength(1)
param nodeTokenSecretName string

@description('The pool this deployment serves (single pool name, used for both SQLFLOW_WORKER_POOL and the scale-target query). Empty takes untargeted runs only.')
param pool string = ''

@description('The private ranges (CIDR, comma separated) the nodes may reach, for an environment integrated into a VNet whose OSDU, storage accounts or proxy resolve to private addresses (SQLFLOW_DELIVERY_PRIVATE_NETWORKS). Empty reaches public addresses only; loopback, link-local and cloud metadata addresses are never reachable.')
param privateNetworks string = ''

@description('Environment variables a node itself reads from the vault, beside its node token: SQLFLOW_CATALOG_DB, the catalog\'s connection, which the node reads and writes the ledger in the `osdu` schema through. Everything a run reads besides is handed to the run by the control plane.')
param secretEnv secretEnvironmentVariable[] = []

@description('Name of a container registry in THIS resource group: the template grants the app identity AcrPull on it and configures the pull. Leave empty for a public registry, or one you authorize yourself via acrLoginServer.')
param acrName string = ''

@description('Login server of a registry outside this resource group (grant AcrPull to the app identity yourself). Ignored when acrName is set.')
param acrLoginServer string = ''

@description('Upper bound for scale out.')
@minValue(1)
param maxReplicas int = 10

@description('vCPU per replica, as a string for exact decimals. Must form a valid Container Apps consumption pair with memory.')
param cpu string = '1.0'

@description('Memory per replica, paired with cpu.')
param memory string = '2.0Gi'

@description('Tags set on the app and its identity.')
param tags { *: string } = {}

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

// Constructed from the vault name (not read from the resource) so the secretEnvSecrets loop below stays computable
// at the start of the deployment, which Bicep requires of for-bodies in variables.
var vaultUri = 'https://${keyVaultName}${environment().suffixes.keyvaultDns}/'
var registryServer = !empty(acrName) ? acr.properties.loginServer : acrLoginServer

// Secrets are pulled from Key Vault by the app's managed identity; their values never appear here. The node token
// is the only credential this tier needs towards OSDU Delivery itself: the node presents it on every node-protocol
// call, and the scale rule presents it to the scale-target endpoint.
var nodeTokenSecrets = [
  {
    name: 'node-token'
    keyVaultUrl: '${vaultUri}secrets/${nodeTokenSecretName}'
    identity: identity.id
  }
]

var secretEnvSecrets = [for (entry, i) in secretEnv: {
  name: 'secret-env-${i}'
  keyVaultUrl: '${vaultUri}secrets/${entry.secretName}'
  identity: identity.id
}]

var baseEnv = [
  // The dispatcher the node polls for work; the run queue lives there, never in the catalog, and everything a
  // run needs (its definition, YAML, lineage context, the values it resolves with) and produces (its trace, its
  // outcome) travels through it.
  {
    name: 'SQLFLOW_URL'
    value: controlPlaneUrl
  }
  // Empty = untargeted runs only, matching the scale-target query below.
  {
    name: 'SQLFLOW_WORKER_POOL'
    value: pool
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
    name: 'SQLFLOW_TOKEN'
    secretRef: 'node-token'
  }
]

// The private ranges the delivery engine may connect to; without them it refuses every private address.
var privateNetworksEnv = empty(privateNetworks) ? [] : [
  {
    name: 'SQLFLOW_DELIVERY_PRIVATE_NETWORKS'
    value: privateNetworks
  }
]

// The OSDU module database connection, and any other variable a node itself needs from the vault.
var secretEnvVars = [for (entry, i) in secretEnv: {
  name: entry.name
  secretRef: 'secret-env-${i}'
}]

// The replica target is answered by the control plane itself (GET /api/v1/node/scale-target?pool=..., node
// scope): the GREATEST of demanded work, the pool's always-on floor, and an active manual override, so the GUI's
// fleet controls steer scaling without the control plane ever calling the orchestrator. Demand is the pool's
// ELIGIBLE queued runs (those no gate holds back: a member behind a lower wave, a group at its cap, or a run of a
// pipeline that is already executing asks for no replica, because no node could take it) divided by what one node
// of the pool executes at once (reported by the nodes themselves on every poll), plus the nodes currently busy (a
// queued-only count reads zero the moment the fleet takes a batch, which would let the platform scale in
// mid-execution and kill replicas carrying live runs). KEDA's metrics-api scaler reads `replicas` against a target
// of 1, so the fleet is held at exactly that number, presenting the node token.
var scaleRules = [
  {
    name: 'replica-target'
    custom: {
      type: 'metrics-api'
      metadata: {
        url: '${controlPlaneUrl}/api/v1/node/scale-target?pool=${uriComponent(pool)}'
        valueLocation: 'replicas'
        targetValue: '1'
        authMode: 'bearer'
      }
      auth: [
        {
          secretRef: 'node-token'
          triggerParameter: 'token'
        }
      ]
    }
  }
]

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
      // No ingress: the node has no inbound surface at all.
      registries: empty(registryServer) ? [] : [
        {
          server: registryServer
          identity: identity.id
        }
      ]
      secrets: concat(nodeTokenSecrets, secretEnvSecrets)
    }
    template: {
      // Let an in-flight run finish on scale-in or revision swap; an interrupted one is requeued anyway. Keep this
      // above the node's drain window (SQLFLOW_WORKER_DRAIN_SECONDS, default 540), or the platform's kill lands
      // mid-drain and severs the delivery the drain exists to save.
      terminationGracePeriodSeconds: 600
      containers: [
        {
          name: 'worker'
          image: image
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: concat(baseEnv, privateNetworksEnv, secretEnvVars)
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: maxReplicas
        rules: scaleRules
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

@description('The client id of the node identity (grant it access to the payload storage and the credentials its pool\'s flows touch beyond this estate\'s vault).')
output identityClientId string = identity.properties.clientId

@description('The principal (object) id of the node identity, for role assignments made outside this template.')
output identityPrincipalId string = identity.properties.principalId

@description('Name of the node identity: the user to create in each database it uses under Entra authentication (CREATE USER [<name>] FROM EXTERNAL PROVIDER).')
output identityName string = identity.name
