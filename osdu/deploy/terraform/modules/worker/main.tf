# One OSDU Delivery worker pool as a Container App, the Terraform mirror of osdu/deploy/bicep/worker.bicep: a compute
# node that polls the control plane's dispatcher for work. Nodes expose nothing (no ingress; outbound HTTPS to the
# control plane, SQL to the ingestion database their flows read, HTTPS to the OSDU endpoint, and git for a pinned run
# without a snapshot) and scale 0..N on the control plane's own replica target through the built-in KEDA metrics-api
# scaler, so an idle pool costs nothing.
#
# THE OSDU LEDGER ON A NODE. The delivery engine reads and writes the ledger in the `osdu` schema per record while it
# plans and delivers. The schema lives in the catalog's database, and SQLFlow itself gives a node no catalog
# connection, so the node is given the catalog's: SQLFLOW_CATALOG_DB in secret_env. Under Entra authentication it
# carries no credential, and the node's identity holds grants on the `osdu` schema alone there. Without it the node
# refuses to start. Everything a run reads beside it (the data databases, the landing storage, the OSDU platform) is
# handed to each run by the control plane, so a node carries nothing else.
#
# Deploy one copy per pool (set name and pool together). Secrets come from the estate's Key Vault, read by the app's
# user-assigned managed identity; that same identity resolves ${keyvault:...} references at run time.

locals {
  key_vault_secrets_user_role_id = "4633458b-17de-408a-b874-0445c86b69e6"
  acr_pull_role_id               = "7f951dda-4ed3-4680-a7ca-43fe172d538d"

  registry_server = var.acr_login_server

  # The node token is the only credential this tier needs towards OSDU Delivery itself: the node presents it on every
  # node-protocol call, and the scale rule presents it to the scale-target endpoint.
  secrets = concat(
    [
      { name = "node-token", secret_name = var.node_token_secret_name },
    ],
    [for i, entry in var.secret_env : { name = "secret-env-${i}", secret_name = entry.secret_name }],
  )

  env = concat(
    [
      # The dispatcher the node polls for work; the run queue lives there, and everything a run needs and produces
      # travels through it.
      { name = "SQLFLOW_URL", value = var.control_plane_url, secret = null },
      # Empty = untargeted runs only, matching the scale-target query below.
      { name = "SQLFLOW_WORKER_POOL", value = var.pool, secret = null },
      # Resolve ${keyvault:...}/cloud-storage credentials at run time via this managed identity.
      { name = "SQLFLOW_AZURE_AUTH", value = "mi", secret = null },
      { name = "AZURE_CLIENT_ID", value = azurerm_user_assigned_identity.this.client_id, secret = null },
    ],
    [
      { name = "SQLFLOW_TOKEN", value = null, secret = "node-token" },
    ],
    # The private ranges the delivery engine may connect to; without them it refuses every private address.
    var.private_networks == "" ? [] : [
      { name = "SQLFLOW_DELIVERY_PRIVATE_NETWORKS", value = var.private_networks, secret = null },
    ],
    # The OSDU module database connection, and any other variable a node itself needs from the vault.
    [for i, entry in var.secret_env : { name = entry.name, value = null, secret = "secret-env-${i}" }],
  )
}

resource "azurerm_user_assigned_identity" "this" {
  name                = "${var.name}-id"
  location            = var.location
  resource_group_name = var.resource_group_name
  tags                = var.tags
}

resource "azurerm_role_assignment" "key_vault_secrets_user" {
  scope              = var.key_vault_id
  role_definition_id = "${regex("^/subscriptions/[^/]+", var.key_vault_id)}/providers/Microsoft.Authorization/roleDefinitions/${local.key_vault_secrets_user_role_id}"
  principal_id       = azurerm_user_assigned_identity.this.principal_id
  principal_type     = "ServicePrincipal"
}

resource "azurerm_role_assignment" "acr_pull" {
  count = var.acr_id != "" ? 1 : 0

  scope              = var.acr_id
  role_definition_id = "${regex("^/subscriptions/[^/]+", var.acr_id)}/providers/Microsoft.Authorization/roleDefinitions/${local.acr_pull_role_id}"
  principal_id       = azurerm_user_assigned_identity.this.principal_id
  principal_type     = "ServicePrincipal"
}

resource "azurerm_container_app" "this" {
  name                         = var.name
  resource_group_name          = var.resource_group_name
  container_app_environment_id = var.container_app_environment_id
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.this.id]
  }

  # No ingress: the node has no inbound surface at all.

  dynamic "registry" {
    for_each = local.registry_server == "" ? [] : [local.registry_server]
    content {
      server   = registry.value
      identity = azurerm_user_assigned_identity.this.id
    }
  }

  dynamic "secret" {
    for_each = local.secrets
    content {
      name                = secret.value.name
      key_vault_secret_id = "${var.key_vault_uri}secrets/${secret.value.secret_name}"
      identity            = azurerm_user_assigned_identity.this.id
    }
  }

  template {
    min_replicas = 0
    max_replicas = var.max_replicas

    # Let an in-flight run finish on scale-in or revision swap; an interrupted one is requeued anyway. Keep this above
    # the node's drain window (SQLFLOW_WORKER_DRAIN_SECONDS, default 540), or the platform's kill lands mid-drain.
    termination_grace_period_seconds = 600

    container {
      name   = "worker"
      image  = var.image
      cpu    = var.cpu
      memory = var.memory

      dynamic "env" {
        for_each = local.env
        content {
          name        = env.value.name
          value       = env.value.value
          secret_name = env.value.secret
        }
      }
    }

    # The replica target is answered by the control plane itself (GET /api/v1/node/scale-target?pool=..., node scope):
    # the greatest of the eligible demand, the pool's always-on floor and an active manual override, plus the nodes
    # currently busy, so the platform never scales in under a run. KEDA's metrics-api scaler reads `replicas` against
    # a target of 1, presenting the node token.
    dynamic "custom_scale_rule" {
      for_each = [1]
      content {
        name             = "replica-target"
        custom_rule_type = "metrics-api"
        metadata = {
          url           = "${var.control_plane_url}/api/v1/node/scale-target?pool=${urlencode(var.pool)}"
          valueLocation = "replicas"
          targetValue   = "1"
          authMode      = "bearer"
        }

        authentication {
          secret_name       = "node-token"
          trigger_parameter = "token"
        }
      }
    }
  }

  # The app resolves its Key Vault secrets (and pulls the image) at creation, so the role grants must exist first.
  depends_on = [
    azurerm_role_assignment.key_vault_secrets_user,
    azurerm_role_assignment.acr_pull,
  ]

  # The image is set when the app is created. After that deploy.ps1 rolls it forward (and rolls it back when a revision
  # does not serve), so a plan never takes an app back to the image it was created with.
  lifecycle {
    ignore_changes = [template[0].container[0].image]
  }
}
