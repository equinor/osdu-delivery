# The OSDU Delivery control plane as an always-on Azure Container App: the warm API the GUI, the CLI and any external
# scheduler call; the Terraform mirror of osdu/deploy/bicep/control-plane.bicep. It runs with a user-assigned managed
# identity that reads its secrets (the catalog connection and the JWT signing key) from Key Vault, and that SAME
# identity is what the engine uses to resolve ${keyvault:...} references at run time (SQLFLOW_AZURE_AUTH=mi), so no
# secret value is ever placed in this module or in app configuration.
#
# The OSDU module's `osdu` schema lives in the catalog database by default; startup applies SQLFlow's catalog
# migrations and then the module's.
#
# A FLOW'S READS RUN HERE. What a person asks of a flow and waits on (a probe of its target, a record read back from
# OSDU, its rows read from the ingestion tables, a preview, a scope's values, the explorer) runs in this app under the
# flow's own credentials, resolving with the same values the control plane hands every run (its settings, the estate's
# configuration), with this app's identity reading the vault. The control plane never delivers; runs, value checks and
# removals stay on the nodes.

locals {
  # The Key Vault Secrets User and AcrPull built-in roles.
  key_vault_secrets_user_role_id = "4633458b-17de-408a-b874-0445c86b69e6"
  acr_pull_role_id               = "7f951dda-4ed3-4680-a7ca-43fe172d538d"

  registry_server = var.acr_login_server

  # Secrets are pulled from Key Vault by the app's managed identity; their values never appear here.
  secrets = concat(
    [
      { name = "catalog-db", secret_name = var.catalog_connection_secret_name },
      { name = "jwt-signing-key", secret_name = var.jwt_signing_key_secret_name },
    ],
    var.bootstrap_admin_password_secret_name == "" ? [] : [
      { name = "bootstrap-admin-password", secret_name = var.bootstrap_admin_password_secret_name },
    ],
  )

  # Every environment variable, each either a plain value or a reference to one of the secrets above.
  env = concat(
    [
      # The catalog connection, which holds the delivery module's `osdu` schema too; the control plane's default
      # ConnectionReference (${env:SQLFLOW_CATALOG_DB}) reads exactly this variable, and startup migrates and verifies
      # the catalog and then the module.
      { name = "SQLFLOW_CATALOG_DB", value = null, secret = "catalog-db" },
      # Resolve ${keyvault:...}/cloud-storage credentials at run time via this managed identity.
      { name = "SQLFLOW_AZURE_AUTH", value = "mi", secret = null },
      { name = "AZURE_CLIENT_ID", value = azurerm_user_assigned_identity.this.client_id, secret = null },
      { name = "ControlPlane__Jwt__SigningKey", value = null, secret = "jwt-signing-key" },
      { name = "ControlPlane__Jwt__Issuer", value = var.jwt_issuer, secret = null },
      { name = "ControlPlane__Jwt__Audience", value = var.jwt_audience, secret = null },
      { name = "ControlPlane__Worker__Enabled", value = var.worker_enabled ? "true" : "false", secret = null },
    ],
    var.bootstrap_admin_password_secret_name == "" ? [] : [
      { name = "ControlPlane__Bootstrap__AdminUsername", value = var.bootstrap_admin_username, secret = null },
      { name = "ControlPlane__Bootstrap__AdminPasswordReference", value = null, secret = "bootstrap-admin-password" },
    ],
    [for i, origin in var.cors_allowed_origins : { name = "ControlPlane__Cors__AllowedOrigins__${i}", value = origin, secret = null }],
    # Proxy trust is all-or-nothing: enabling it while trusting no networks fails startup validation, so the enable
    # switch rides on the network list being non-empty.
    length(var.proxy_known_networks) == 0 ? [] : concat(
      [{ name = "ControlPlane__Proxy__Enabled", value = "true", secret = null }],
      [for i, cidr in var.proxy_known_networks : { name = "ControlPlane__Proxy__KnownNetworks__${i}", value = cidr, secret = null }],
    ),
    # Entra SSO turns on once at least one allowed tenant id and a client id are present; each tenant is its own
    # indexed variable, since the control plane binds AllowedTenantIds as a list.
    (length(var.azure_ad_allowed_tenant_ids) == 0 || var.azure_ad_client_id == "") ? [] : concat(
      [for i, tenant in var.azure_ad_allowed_tenant_ids : { name = "ControlPlane__AzureAd__AllowedTenantIds__${i}", value = tenant, secret = null }],
      [
        { name = "ControlPlane__AzureAd__ClientId", value = var.azure_ad_client_id, secret = null },
        { name = "ControlPlane__AzureAd__DefaultRole", value = var.azure_ad_default_role, secret = null },
      ],
    ),
    # The GUI chat assistant (Foundry mode: this identity signs in to the account, no API key). The chat endpoints
    # stay mapped when it is off and report the switch via /chat/capabilities.
    !var.assistant_enabled ? [] : concat(
      [
        { name = "ControlPlane__Assistant__Enabled", value = "true", secret = null },
        { name = "ControlPlane__Assistant__Foundry__ProjectEndpoint", value = var.assistant_foundry_project_endpoint, secret = null },
        { name = "ControlPlane__Assistant__Foundry__ModelDeploymentName", value = var.assistant_foundry_model_deployment_name, secret = null },
        { name = "ControlPlane__Assistant__Mcp__ServerUrl", value = var.assistant_mcp_server_url, secret = null },
      ],
      var.assistant_foundry_transcription_deployment_name == "" ? [] : [
        { name = "ControlPlane__Assistant__Foundry__TranscriptionDeploymentName", value = var.assistant_foundry_transcription_deployment_name, secret = null },
      ],
    ),
    !var.ai_context_enabled ? [] : [
      { name = "ControlPlane__AiContext__Enabled", value = "true", secret = null },
    ],
    var.private_networks == "" ? [] : [
      { name = "SQLFLOW_DELIVERY_PRIVATE_NETWORKS", value = var.private_networks, secret = null },
    ],
    [for name in sort(keys(var.settings)) : { name = name, value = var.settings[name], secret = null }],
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

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

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
    min_replicas = var.min_replicas
    max_replicas = var.max_replicas

    container {
      name   = "control-plane"
      image  = var.image
      cpu    = 0.5
      memory = "1Gi"

      dynamic "env" {
        for_each = local.env
        content {
          name        = env.value.name
          value       = env.value.value
          secret_name = env.value.secret
        }
      }

      liveness_probe {
        transport        = "HTTP"
        port             = 8080
        path             = "/health/live"
        interval_seconds = 30
      }

      readiness_probe {
        transport        = "HTTP"
        port             = 8080
        path             = "/health/ready"
        interval_seconds = 30
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
