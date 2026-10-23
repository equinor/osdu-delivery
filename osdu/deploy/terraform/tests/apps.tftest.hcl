# What each app module puts in its Container App, run against a mocked azurerm provider: the environment variables,
# the vault references and the scale rules the Bicep templates set.

mock_provider "azurerm" {
  mock_data "azurerm_client_config" {
    defaults = {
      tenant_id       = "11111111-1111-1111-1111-111111111111"
      subscription_id = "00000000-0000-0000-0000-000000000000"
    }
  }

  mock_resource "azurerm_log_analytics_workspace" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.OperationalInsights/workspaces/osdu-delivery-logs"
    }
  }

  mock_resource "azurerm_container_app" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.App/containerApps/osdu-delivery-app"
    }
  }

  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/osdu-delivery-app-id"
      principal_id = "22222222-2222-2222-2222-222222222222"
      client_id    = "33333333-3333-3333-3333-333333333333"
    }
  }
}

variables {
  resource_group_name          = "osdu-delivery-test"
  location                     = "westeurope"
  container_app_environment_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.App/managedEnvironments/osdu-delivery-env"
  key_vault_id                 = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.KeyVault/vaults/osdu-kv-test"
  key_vault_uri                = "https://osdu-kv-test.vault.azure.net/"
}

run "the_control_plane_without_the_assistant" {
  command = apply

  module {
    source = "./modules/control-plane"
  }

  variables {
    image                                = "osdutest.azurecr.io/osdu-delivery-control-plane:abc1234"
    worker_enabled                       = false
    bootstrap_admin_password_secret_name = "osdu-delivery-admin-password"
    settings                             = { "Osdu__Properties__OSDU_DATA_DB" = "$${keyvault:osdu-kv-test/osdu-delivery-conn-osdu-data-db}" }
  }

  assert {
    condition     = !contains([for e in azurerm_container_app.this.template[0].container[0].env : e.name], "ControlPlane__Assistant__Enabled")
    error_message = "The assistant settings are absent while it is off."
  }

  assert {
    condition     = !contains([for e in azurerm_container_app.this.template[0].container[0].env : e.name], "ControlPlane__AiContext__Enabled")
    error_message = "AI context is absent while it is off."
  }

  assert {
    condition = (
      one([for e in azurerm_container_app.this.template[0].container[0].env : e.value if e.name == "Osdu__Properties__OSDU_DATA_DB"]) == "$${keyvault:osdu-kv-test/osdu-delivery-conn-osdu-data-db}" &&
      !contains([for e in azurerm_container_app.this.template[0].container[0].env : e.name], "OSDU_DATA_DB")
    )
    error_message = "A value every run resolves with is a setting holding a vault reference, which the run resolves; the app itself holds no flow's variable."
  }

  assert {
    condition     = alltrue([for s in azurerm_container_app.this.secret : startswith(s.key_vault_secret_id, "https://osdu-kv-test.vault.azure.net/secrets/")])
    error_message = "Every secret is a reference into the estate's vault, never a value."
  }

  assert {
    condition     = one([for e in azurerm_container_app.this.template[0].container[0].env : e.value if e.name == "ControlPlane__Worker__Enabled"]) == "false"
    error_message = "The in-process worker is off when dedicated nodes take the work."
  }
}

run "the_control_plane_with_the_assistant" {
  command = apply

  module {
    source = "./modules/control-plane"
  }

  variables {
    image                                           = "osdutest.azurecr.io/osdu-delivery-control-plane:abc1234"
    assistant_enabled                               = true
    assistant_foundry_project_endpoint              = "https://osdu-foundry-test.services.ai.azure.com/api/projects/osdu-delivery"
    assistant_foundry_model_deployment_name         = "gpt-5.1"
    assistant_foundry_transcription_deployment_name = "gpt-4o-mini-transcribe"
    assistant_mcp_server_url                        = "https://osdu-delivery-mcp.calm-sea.westeurope.azurecontainerapps.io/mcp"
    ai_context_enabled                              = true
  }

  assert {
    condition = alltrue([for name in [
      "ControlPlane__Assistant__Enabled",
      "ControlPlane__Assistant__Foundry__ProjectEndpoint",
      "ControlPlane__Assistant__Foundry__ModelDeploymentName",
      "ControlPlane__Assistant__Foundry__TranscriptionDeploymentName",
      "ControlPlane__Assistant__Mcp__ServerUrl",
      "ControlPlane__AiContext__Enabled",
    ] : contains([for e in azurerm_container_app.this.template[0].container[0].env : e.name], name)])
    error_message = "Every assistant and AI context setting is set while they are on."
  }

  assert {
    condition     = one([for e in azurerm_container_app.this.template[0].container[0].env : e.value if e.name == "ControlPlane__Assistant__Mcp__ServerUrl"]) == "https://osdu-delivery-mcp.calm-sea.westeurope.azurecontainerapps.io/mcp"
    error_message = "The assistant calls the MCP server it is given."
  }
}

run "an_mcp_url_that_is_not_https_is_refused" {
  command = plan

  module {
    source = "./modules/control-plane"
  }

  variables {
    image                    = "osdutest.azurecr.io/osdu-delivery-control-plane:abc1234"
    assistant_mcp_server_url = "http://osdu-delivery-mcp/mcp"
  }

  expect_failures = [var.assistant_mcp_server_url]
}

run "a_worker_carries_its_node_token_and_the_ledger_s_connection_alone" {
  command = apply

  module {
    source = "./modules/worker"
  }

  variables {
    image                  = "osdutest.azurecr.io/osdu-delivery-worker:abc1234"
    control_plane_url      = "https://osdu-delivery-control-plane.calm-sea.westeurope.azurecontainerapps.io"
    node_token_secret_name = "osdu-delivery-node-token"
    secret_env             = [{ name = "SQLFLOW_CATALOG_DB", secret_name = "osdu-delivery-catalog-db" }]
  }

  assert {
    condition     = sort([for e in azurerm_container_app.this.template[0].container[0].env : e.name if e.secret_name != null]) == tolist(["SQLFLOW_CATALOG_DB", "SQLFLOW_TOKEN"])
    error_message = "A node reads its node token and the catalog's connection, which holds the ledger, from the vault, and nothing else: its runs are handed the rest."
  }

  assert {
    condition = (
      sort([for s in azurerm_container_app.this.secret : s.key_vault_secret_id]) == tolist([
        "https://osdu-kv-test.vault.azure.net/secrets/osdu-delivery-catalog-db",
        "https://osdu-kv-test.vault.azure.net/secrets/osdu-delivery-node-token",
      ])
    )
    error_message = "Every secret is a reference into the estate's vault, never a value."
  }
}

run "a_worker_with_its_node_token_scales_on_the_replica_target" {
  command = apply

  module {
    source = "./modules/worker"
  }

  variables {
    image                  = "osdutest.azurecr.io/osdu-delivery-worker:abc1234"
    control_plane_url      = "https://osdu-delivery-control-plane.calm-sea.westeurope.azurecontainerapps.io"
    node_token_secret_name = "osdu-delivery-node-token"
    pool                   = "heavy"
  }

  assert {
    condition     = azurerm_container_app.this.template[0].custom_scale_rule[0].metadata.url == "https://osdu-delivery-control-plane.calm-sea.westeurope.azurecontainerapps.io/api/v1/node/scale-target?pool=heavy"
    error_message = "The scale rule reads the pool's replica target from the control plane."
  }

  assert {
    condition     = azurerm_container_app.this.template[0].custom_scale_rule[0].authentication[0].secret_name == "node-token"
    error_message = "The scale rule presents the node token."
  }

  assert {
    condition     = azurerm_container_app.this.template[0].termination_grace_period_seconds == 600 && azurerm_container_app.this.template[0].min_replicas == 0
    error_message = "The pool scales to zero and gives a node its drain window to stop."
  }

  assert {
    condition     = one([for e in azurerm_container_app.this.template[0].container[0].env : e.value if e.name == "SQLFLOW_WORKER_POOL"]) == "heavy"
    error_message = "The node serves its pool."
  }
}

run "the_mcp_server_answers_only_on_its_own_host" {
  command = apply

  module {
    source = "./modules/mcp"
  }

  variables {
    image             = "osdutest.azurecr.io/osdu-delivery-mcp:abc1234"
    control_plane_url = "https://osdu-delivery-control-plane.calm-sea.westeurope.azurecontainerapps.io"
    gui_url           = "https://osdu-delivery-gui.calm-sea.westeurope.azurecontainerapps.io"
    allowed_hosts     = "osdu-delivery-mcp.calm-sea.westeurope.azurecontainerapps.io"
  }

  assert {
    condition     = one([for e in azurerm_container_app.this.template[0].container[0].env : e.value if e.name == "SQLFLOW_MCP_HTTP_ALLOWED_HOSTS"]) == "osdu-delivery-mcp.calm-sea.westeurope.azurecontainerapps.io"
    error_message = "The MCP server is told the one host it answers on."
  }

  assert {
    condition     = azurerm_container_app.this.template[0].min_replicas == 1 && azurerm_container_app.this.template[0].max_replicas == 1
    error_message = "Exactly one MCP replica, since sessions live in its memory."
  }

  assert {
    condition     = azurerm_container_app.this.ingress[0].external_enabled && !azurerm_container_app.this.ingress[0].allow_insecure_connections
    error_message = "The MCP server is public, over TLS only."
  }
}

run "an_app_pulls_from_its_registry_with_its_own_identity" {
  command = apply

  module {
    source = "./modules/gui"
  }

  variables {
    image            = "osdutest.azurecr.io/osdu-delivery-gui:abc1234"
    api_base_url     = "https://osdu-delivery-control-plane.calm-sea.westeurope.azurecontainerapps.io"
    acr_id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.ContainerRegistry/registries/osdutest"
    acr_login_server = "osdutest.azurecr.io"
  }

  assert {
    condition     = azurerm_role_assignment.acr_pull[0].scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.ContainerRegistry/registries/osdutest"
    error_message = "The app identity is granted AcrPull on the registry it is given."
  }

  assert {
    condition     = azurerm_role_assignment.acr_pull[0].role_definition_id == "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d"
    error_message = "The role id is the registry's subscription's, known without reading anything."
  }

  assert {
    condition     = azurerm_container_app.this.registry[0].server == "osdutest.azurecr.io" && azurerm_container_app.this.workload_profile_name == "Consumption"
    error_message = "The app pulls from that registry and runs on the environment's Consumption profile."
  }
}

# The same app planned again with another image, as when the estate's Terraform is applied after deploy.ps1 rolled the app
# forward: the image the app serves stays.
run "a_later_apply_leaves_the_image_the_app_serves" {
  command = apply

  module {
    source = "./modules/gui"
  }

  variables {
    image            = "osdutest.azurecr.io/osdu-delivery-gui:def5678"
    api_base_url     = "https://osdu-delivery-control-plane.calm-sea.westeurope.azurecontainerapps.io"
    acr_id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.ContainerRegistry/registries/osdutest"
    acr_login_server = "osdutest.azurecr.io"
  }

  assert {
    condition     = azurerm_container_app.this.template[0].container[0].image == "osdutest.azurecr.io/osdu-delivery-gui:abc1234"
    error_message = "An app's image is set when it is created; deploy.ps1 rolls it forward from there, and an apply leaves it."
  }
}

run "a_node_reads_the_vault_under_the_vault_s_own_subscription" {
  command = apply

  module {
    source = "./modules/worker"
  }

  variables {
    image                  = "osdutest.azurecr.io/osdu-delivery-worker:abc1234"
    control_plane_url      = "https://osdu-delivery-control-plane.calm-sea.westeurope.azurecontainerapps.io"
    node_token_secret_name = "osdu-delivery-node-token"
  }

  assert {
    condition     = azurerm_role_assignment.key_vault_secrets_user.role_definition_id == "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6"
    error_message = "Key Vault Secrets User is assigned under the vault's subscription."
  }

  assert {
    condition     = length(azurerm_role_assignment.acr_pull) == 0 && length(azurerm_container_app.this.registry) == 0
    error_message = "Without a registry nothing is granted and the image is pulled anonymously."
  }
}
