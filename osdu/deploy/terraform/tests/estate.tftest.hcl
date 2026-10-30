# The estate's switches and wiring, run against mocked providers: nothing reaches Azure or Entra, so these run anywhere
# terraform init -backend=false has run (terraform test).

mock_provider "azurerm" {
  mock_data "azurerm_resource_group" {
    defaults = {
      id       = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test"
      name     = "osdu-delivery-test"
      location = "westeurope"
    }
  }

  mock_data "azurerm_client_config" {
    defaults = {
      tenant_id       = "11111111-1111-1111-1111-111111111111"
      subscription_id = "00000000-0000-0000-0000-000000000000"
    }
  }

  mock_resource "azurerm_container_app_environment" {
    defaults = {
      id             = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.App/managedEnvironments/osdu-delivery-env"
      default_domain = "calm-sea.westeurope.azurecontainerapps.io"
    }
  }

  mock_resource "azurerm_key_vault" {
    defaults = {
      id        = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.KeyVault/vaults/osdu-kv-test"
      vault_uri = "https://osdu-kv-test.vault.azure.net/"
    }
  }

  mock_resource "azurerm_mssql_server" {
    defaults = {
      id                          = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.Sql/servers/osdu-sql-test"
      fully_qualified_domain_name = "osdu-sql-test.database.windows.net"
    }
  }

  mock_resource "azurerm_log_analytics_workspace" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.OperationalInsights/workspaces/osdu-delivery-logs"
    }
  }

  mock_resource "azurerm_cognitive_account_project" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.CognitiveServices/accounts/osdu-foundry-test/projects/osdu-delivery"
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

  mock_resource "azurerm_storage_account" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.Storage/storageAccounts/osdulandingtest"
    }
  }

  mock_resource "azurerm_resource_deployment_script_azure_power_shell" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.Resources/deploymentScripts/osdu-delivery-setup"
    }
  }

  mock_data "azurerm_container_registry" {
    defaults = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.ContainerRegistry/registries/osdutest"
      login_server = "osdutest.azurecr.io"
    }
  }

  mock_resource "azurerm_cognitive_account" {
    defaults = {
      id       = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-delivery-test/providers/Microsoft.CognitiveServices/accounts/osdu-foundry-test"
      endpoint = "https://osdu-foundry-test.cognitiveservices.azure.com/"
    }
  }
}

mock_provider "azuread" {
  mock_resource "azuread_application" {
    defaults = {
      client_id = "44444444-4444-4444-4444-444444444444"
    }
  }
  # The deploying principal, made owner of the sign-in registration and its service principal: an object id is a UUID.
  mock_data "azuread_client_config" {
    defaults = {
      object_id = "55555555-5555-5555-5555-555555555555"
    }
  }
}

variables {
  resource_group_name = "osdu-delivery-test"
  control_plane_image = "osdutest.azurecr.io/osdu-delivery-control-plane:abc1234"
  worker_image        = "osdutest.azurecr.io/osdu-delivery-worker:abc1234"
  gui_image           = "osdutest.azurecr.io/osdu-delivery-gui:abc1234"

  # The default: the apps sign in as their managed identities, and this group owns every database.
  sql_admins = [{ name = "osdu-delivery-sql-admins", object_id = "77777777-7777-7777-7777-777777777777", type = "group" }]

  # The data database the flows work in, on the estate's server.
  sql_connections = { OSDU_DATA_DB = { database = "OsduData" } }

  # One partition to work against, its client secret put in the estate's vault by hand.
  osdu_partitions = {
    dev = {
      description = "The development platform"
      url         = "https://osdu-dev.example.com"
      token_url   = "https://login.example.com/tenant/oauth2/v2.0/token"
      scope       = "https://osdu.example.com/.default"
      client_id   = "55555555-5555-5555-5555-555555555555"
      legal_tag   = "dev-legal"
    }
  }
}

run "the_core_estate_has_no_assistant_and_its_configuration_names_every_value" {
  command = apply

  assert {
    condition     = output.chat_assistant_enabled == false && output.mcp_url == "" && output.ai_foundry_endpoint == ""
    error_message = "Without an MCP image and a Foundry model there is no assistant, MCP server or Foundry account."
  }

  assert {
    condition     = length(module.mcp) == 0 && length(module.ai_foundry) == 0
    error_message = "The optional tiers are created only when asked for."
  }

  assert {
    condition     = length(azurerm_mssql_database.catalog) == 1 && strcontains(azurerm_key_vault_secret.catalog_db.value, "Initial Catalog=OsduDelivery;")
    error_message = "The osdu schema lives in the catalog database, reached through the catalog's one connection."
  }

  assert {
    condition = (
      keys(azurerm_mssql_database.connection) == ["OsduData"] &&
      azurerm_key_vault_secret.connection["OSDU_DATA_DB"].name == "osdu-delivery-conn-osdu-data-db" &&
      strcontains(azurerm_key_vault_secret.connection["OSDU_DATA_DB"].value, "Server=tcp:osdu-sql-test.database.windows.net,1433;Initial Catalog=OsduData;")
    )
    error_message = "The data database the flows work in is created beside the metadata database, and its connection written to the vault."
  }

  assert {
    condition = alltrue([for value in [
      azurerm_key_vault_secret.catalog_db.value,
      azurerm_key_vault_secret.connection["OSDU_DATA_DB"].value,
    ] : strcontains(value, "Authentication=Active Directory Default;") && !strcontains(value, "Password=") && !strcontains(value, "User ID=")])
    error_message = "By default the apps sign in as their managed identities, with no credential in any connection."
  }

  assert {
    condition = (
      local.control_plane_settings["Osdu__Properties__SQLFLOW_CATALOG_DB"] == "$${keyvault:${azurerm_key_vault.this.name}/osdu-delivery-catalog-db}" &&
      local.control_plane_settings["Osdu__Properties__OSDU_DATA_DB"] == "$${keyvault:${azurerm_key_vault.this.name}/osdu-delivery-conn-osdu-data-db}" &&
      output.sql_connections == { OSDU_DATA_DB = "osdu-delivery-conn-osdu-data-db" }
    )
    error_message = "Every run is given the metadata database and each SQL connection by the names the flows spell, as references into the vault."
  }

  assert {
    condition     = local.control_plane_settings["ControlPlane__Bootstrap__NodeTokenReference"] == "$${keyvault:${azurerm_key_vault.this.name}/osdu-delivery-node-token}"
    error_message = "The control plane is told where the node credential the workers present is, never the credential."
  }

  assert {
    condition     = local.worker_secret_env == [{ name = "SQLFLOW_CATALOG_DB", secret_name = "osdu-delivery-catalog-db" }]
    error_message = "A node carries its identity and the catalog's connection, which holds the ledger, alone: everything its runs read is handed to each run."
  }

  assert {
    condition = (
      length([for name in keys(local.control_plane_settings) : name if startswith(name, "ControlPlane__Bootstrap__Repositories__")]) == 0 &&
      length(azurerm_storage_account.landing) == 0 && output.landing_url == ""
    )
    error_message = "Without repositories or landing storage, none is configured."
  }

  assert {
    condition = (
      azurerm_mssql_server.this[0].azuread_administrator[0].azuread_authentication_only &&
      azurerm_mssql_server.this[0].azuread_administrator[0].login_username == "osdu-delivery-setup" &&
      azurerm_mssql_server.this[0].azuread_administrator[0].object_id == azurerm_user_assigned_identity.setup.client_id &&
      azurerm_mssql_server.this[0].administrator_login_password == null
    )
    error_message = "The created server is administered by the estate's setup identity and has no SQL login at all."
  }

  assert {
    condition     = output.entra_client_id == "44444444-4444-4444-4444-444444444444" && output.entra_assignment_reminder != ""
    error_message = "The provisioned registration's client id reaches the control plane, and the reminder says no group is assigned."
  }

  assert {
    condition     = output.entra_redirect_uri == output.gui_url
    error_message = "The GUI origin is the registration's redirect URI."
  }

  assert {
    condition     = output.sql_server_fqdn == "osdu-sql-test.database.windows.net" && output.setup_identity_name == "osdu-delivery-setup"
    error_message = "The created server's address and the identity that administers it are reported."
  }

  assert {
    condition     = anytrue([for profile in azurerm_container_app_environment.this.workload_profile : profile.name == "Consumption" && profile.workload_profile_type == "Consumption"])
    error_message = "The environment declares the Consumption profile Azure gives every environment."
  }
}

run "the_deployment_generates_the_secrets_nobody_provides_and_writes_none_itself" {
  command = apply

  assert {
    condition = (
      azurerm_resource_deployment_script_azure_power_shell.secrets.command_line == "-Step Secrets" &&
      tolist(azurerm_resource_deployment_script_azure_power_shell.secrets.identity[0].identity_ids) == tolist([azurerm_user_assigned_identity.setup.id]) &&
      azurerm_role_assignment.setup_secrets_officer.role_definition_name == "Key Vault Secrets Officer" &&
      azurerm_role_assignment.setup_secrets_officer.scope == azurerm_key_vault.this.id
    )
    error_message = "The secrets step runs as the setup identity, a secrets officer of the estate's vault."
  }

  assert {
    condition = (
      jsondecode(one([for e in azurerm_resource_deployment_script_azure_power_shell.secrets.environment_variable : e.value if e.name == "SETUP_SPEC"])).vault == azurerm_key_vault.this.name &&
      [for g in jsondecode(one([for e in azurerm_resource_deployment_script_azure_power_shell.secrets.environment_variable : e.value if e.name == "SETUP_SPEC"])).generated : "${g.name}/${g.kind}"] == [
        "osdu-delivery-jwt-signing-key/signing-key",
        "osdu-delivery-admin-password/password",
        "osdu-delivery-node-token/node-token",
      ] &&
      jsondecode(one([for e in azurerm_resource_deployment_script_azure_power_shell.secrets.environment_variable : e.value if e.name == "SETUP_SPEC"])).sql == null
    )
    error_message = "The step generates the signing key, the initial admin's password and the node credential into the vault, by name only."
  }

  assert {
    condition     = output.admin_password_secret == "osdu-delivery-admin-password" && output.key_vault_name == azurerm_key_vault.this.name
    error_message = "Where the initial admin's password is read from is reported."
  }
}

run "the_grants_are_made_inside_the_deployment_on_a_server_created_here" {
  command = apply

  assert {
    condition = (
      length(azurerm_resource_deployment_script_azure_power_shell.grants) == 1 &&
      azurerm_resource_deployment_script_azure_power_shell.grants[0].command_line == "-Step Grants" &&
      jsondecode(one([for e in azurerm_resource_deployment_script_azure_power_shell.grants[0].environment_variable : e.value if e.name == "SETUP_SPEC"])).sql.server == "osdu-sql-test.database.windows.net" &&
      [for d in jsondecode(one([for e in azurerm_resource_deployment_script_azure_power_shell.grants[0].environment_variable : e.value if e.name == "SETUP_SPEC"])).sql.databases : d.name] == ["OsduData", "OsduDelivery"]
    )
    error_message = "The grants step is given the server and each database the estate has."
  }

  assert {
    condition = (
      [for u in local.sql_setup_grouped["OsduDelivery"][0].users : "${u.name}/${u.id}/${u.type}"] == [
        "osdu-delivery-control-plane-id/33333333-3333-3333-3333-333333333333/E",
        "osdu-delivery-worker-id/33333333-3333-3333-3333-333333333333/E",
        "osdu-delivery-sql-admins/77777777-7777-7777-7777-777777777777/X",
      ]
    )
    error_message = "Each app signs in as its identity's client id, and an admin group by its object id."
  }

  assert {
    condition = alltrue([for statement in [
      "ALTER ROLE db_owner ADD MEMBER [osdu-delivery-control-plane-id];",
      "IF SCHEMA_ID(N'osdu') IS NULL EXEC(N'CREATE SCHEMA [osdu];');",
      "GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE, ALTER, REFERENCES ON SCHEMA::osdu TO [osdu-delivery-worker-id];",
      "ALTER ROLE db_owner ADD MEMBER [osdu-delivery-sql-admins];",
    ] : contains(one([for d in local.sql_setup_databases : d.statements if d.name == "OsduDelivery"]), statement)])
    error_message = "The control plane and the admins own the metadata database; the worker reaches the osdu schema."
  }

  assert {
    condition = alltrue([for statement in [
      "ALTER ROLE db_datareader ADD MEMBER [osdu-delivery-control-plane-id];",
      "ALTER ROLE db_ddladmin ADD MEMBER [osdu-delivery-worker-id];",
      "ALTER ROLE db_datawriter ADD MEMBER [osdu-delivery-worker-id];",
      "ALTER ROLE db_owner ADD MEMBER [osdu-delivery-sql-admins];",
    ] : contains(one([for d in local.sql_setup_databases : d.statements if d.name == "OsduData"]), statement)])
    error_message = "The worker creates, loads and reads the flows' tables in a data database it writes; the control plane reads it."
  }

  assert {
    condition     = output.sql_setup_script == ""
    error_message = "No script is left for a person when the deployment grants the users itself."
  }
}

run "a_registry_named_here_is_read_once_at_the_root" {
  command = apply

  variables {
    acr_name = "osdutest"
  }

  assert {
    condition     = length(data.azurerm_container_registry.this) == 1
    error_message = "The registry is read at the root, never inside a module."
  }
}

run "the_mcp_server_and_a_foundry_model_turn_the_assistant_on" {
  command = apply

  variables {
    mcp_image             = "osdutest.azurecr.io/osdu-delivery-mcp:abc1234"
    ai_foundry_name       = "osdu-foundry-test"
    ai_foundry_model_name = "gpt-5.1"
  }

  assert {
    condition     = output.chat_assistant_enabled
    error_message = "The MCP server and a Foundry model together turn the assistant on."
  }

  assert {
    condition     = startswith(output.mcp_url, "https://") && endswith(output.mcp_url, "/mcp")
    error_message = "The MCP endpoint is reported."
  }

  assert {
    condition     = output.ai_foundry_project_endpoint == "https://osdu-foundry-test.services.ai.azure.com/api/projects/osdu-delivery"
    error_message = "The project endpoint follows from the account and project names."
  }
}

run "a_foundry_account_without_a_model_leaves_the_assistant_off" {
  command = apply

  variables {
    mcp_image       = "osdutest.azurecr.io/osdu-delivery-mcp:abc1234"
    ai_foundry_name = "osdu-foundry-test"
  }

  assert {
    condition     = output.chat_assistant_enabled == false && length(module.ai_foundry) == 1 && output.mcp_url != ""
    error_message = "The account and the MCP server are created, but without a model the assistant stays off."
  }
}

run "an_existing_sql_server_is_used_as_given" {
  command = apply

  variables {
    existing_sql_server = "osdu-mi.public.abc123.database.windows.net,3342"
  }

  assert {
    condition = (
      length(azurerm_mssql_server.this) == 0 && length(azurerm_mssql_database.catalog) == 0 &&
      length(azurerm_mssql_database.connection) == 0 && length(azurerm_mssql_firewall_rule.allow_azure_services) == 0
    )
    error_message = "Nothing is created on an existing server."
  }

  assert {
    condition = (
      strcontains(azurerm_key_vault_secret.catalog_db.value, "Server=tcp:osdu-mi.public.abc123.database.windows.net,3342;") &&
      strcontains(azurerm_key_vault_secret.connection["OSDU_DATA_DB"].value, "Server=tcp:osdu-mi.public.abc123.database.windows.net,3342;Initial Catalog=OsduData;")
    )
    error_message = "An existing address keeps its own port, and each connection names its existing database."
  }

  assert {
    condition     = output.sql_server_fqdn == "osdu-mi.public.abc123.database.windows.net,3342"
    error_message = "The existing address is reported."
  }

  assert {
    condition     = length(azurerm_resource_deployment_script_azure_power_shell.grants) == 0
    error_message = "The deployment does not administer a server it did not create."
  }

  assert {
    condition = alltrue([for line in [
      "IF USER_ID(N'osdu-delivery-control-plane-id') IS NULL CREATE USER [osdu-delivery-control-plane-id] FROM EXTERNAL PROVIDER;",
      "IF USER_ID(N'osdu-delivery-sql-admins') IS NULL CREATE USER [osdu-delivery-sql-admins] FROM EXTERNAL PROVIDER;",
      "ALTER ROLE db_owner ADD MEMBER [osdu-delivery-control-plane-id];",
      "GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE, ALTER, REFERENCES ON SCHEMA::osdu TO [osdu-delivery-worker-id];",
      "ALTER ROLE db_datawriter ADD MEMBER [osdu-delivery-worker-id];",
      "-- OsduDelivery: the catalog, and the osdu schema beside it",
      "-- OsduData: OSDU_DATA_DB, the data the flows land, load and deliver from",
      "an Entra administrator of osdu-mi.public.abc123.database.windows.net,3342,",
    ] : strcontains(output.sql_setup_script, line)])
    error_message = "The same users and roles are left for that server's administrator to run, database by database."
  }
}

run "an_existing_managed_instance_with_its_databases_signs_in_with_entra" {
  command = apply

  variables {
    existing_sql_server   = "osdu-mi.abc123def456.database.windows.net"
    sql_authentication    = "entra"
    sql_admin_password    = null
    catalog_database_name = "OsduDelivery"
    sql_connections       = { OSDU_DATA_DB = { database = "OsduDataProd" } }
  }

  assert {
    condition = alltrue([for value in [
      azurerm_key_vault_secret.catalog_db.value,
      azurerm_key_vault_secret.connection["OSDU_DATA_DB"].value,
    ] : strcontains(value, "Authentication=Active Directory Default;") && !strcontains(value, "Password=") && !strcontains(value, "User ID=")])
    error_message = "Under Entra authentication no connection carries a credential."
  }

  assert {
    condition     = strcontains(azurerm_key_vault_secret.connection["OSDU_DATA_DB"].value, "Server=tcp:osdu-mi.abc123def456.database.windows.net,1433;Initial Catalog=OsduDataProd;")
    error_message = "The instance's private address gets the standard port, and each connection names its existing database."
  }

  assert {
    condition     = output.control_plane_identity_name == "osdu-delivery-control-plane-id" && output.worker_identity_name == "osdu-delivery-worker-id"
    error_message = "The identities the database users are created for are reported by name."
  }
}

run "one_database_may_hold_the_metadata_and_the_data" {
  command = apply

  variables {
    existing_sql_server   = "osdu-mi.abc123def456.database.windows.net"
    catalog_database_name = "OsduDelivery"
    sql_connections       = { OSDU_DATA_DB = { database = "OsduDelivery" } }
  }

  assert {
    condition = (
      length(regexall("-- OsduDelivery:", output.sql_setup_script)) == 1 &&
      strcontains(output.sql_setup_script, "-- OsduDelivery: the catalog, and the osdu schema beside it; OSDU_DATA_DB, the data the flows land, load and deliver from") &&
      length(regexall("CREATE USER \\[osdu-delivery-worker-id\\]", output.sql_setup_script)) == 1 &&
      length(regexall("ALTER ROLE db_datawriter ADD MEMBER", output.sql_setup_script)) == 1
    )
    error_message = "A database playing both roles gets one part of the script, each statement once."
  }

  assert {
    condition     = strcontains(output.sql_setup_script, "an Entra administrator of osdu-mi.abc123def456.database.windows.net,")
    error_message = "The script names the server it is for."
  }
}

run "an_estate_that_names_no_connection_keeps_its_data_database" {
  command = apply

  variables {
    sql_connections = null
  }

  assert {
    condition = (
      keys(azurerm_mssql_database.connection) == ["OsduData"] &&
      local.control_plane_settings["Osdu__Properties__OSDU_DATA_DB"] == "$${keyvault:${azurerm_key_vault.this.name}/osdu-delivery-conn-osdu-data-db}"
    )
    error_message = "Without sql_connections the estate has the data database every estate has had, as OSDU_DATA_DB."
  }
}

run "a_connection_to_the_metadata_database_creates_no_database_of_its_own" {
  command = apply

  variables {
    sql_connections = { OSDU_DATA_DB = { database = "OsduDelivery" } }
  }

  assert {
    condition = (
      length(azurerm_mssql_database.connection) == 0 &&
      strcontains(azurerm_key_vault_secret.connection["OSDU_DATA_DB"].value, "Initial Catalog=OsduDelivery;") &&
      [for d in local.sql_setup_databases : d.name] == ["OsduDelivery"]
    )
    error_message = "A connection to the metadata database is written and granted, and creates nothing more."
  }
}

run "the_sql_connections_reach_every_run_by_their_names" {
  command = apply

  variables {
    sql_connections = {
      OSDU_DATA_DB = { database = "OsduData" }
      REPORTING_DB = { database = "OsduData", access = "read" }
      ARCHIVE_DB   = { database = "OsduArchive", access = "read" }
      RESTORED_DB  = { database = "OsduRestored", create = false }
      PARTNER_DB   = { secret_name = "partner-db-connection" }
    }
  }

  assert {
    condition     = keys(azurerm_mssql_database.connection) == ["OsduArchive", "OsduData"]
    error_message = "One database is created per name, whichever connections name it, and none a connection says exists already."
  }

  assert {
    condition     = keys(azurerm_key_vault_secret.connection) == ["ARCHIVE_DB", "OSDU_DATA_DB", "REPORTING_DB", "RESTORED_DB"]
    error_message = "A connection on the estate's server is written to the vault; one elsewhere is read from the secret put there by hand."
  }

  assert {
    condition = (
      local.control_plane_settings["Osdu__Properties__PARTNER_DB"] == "$${keyvault:${azurerm_key_vault.this.name}/partner-db-connection}" &&
      local.control_plane_settings["Osdu__Properties__ARCHIVE_DB"] == "$${keyvault:${azurerm_key_vault.this.name}/osdu-delivery-conn-archive-db}" &&
      local.control_plane_settings["Osdu__Properties__REPORTING_DB"] == "$${keyvault:${azurerm_key_vault.this.name}/osdu-delivery-conn-reporting-db}"
    )
    error_message = "Every connection reaches every run as a reference to its vault secret."
  }

  assert {
    condition = (
      !contains(one([for d in local.sql_setup_databases : d.statements if d.name == "OsduArchive"]), "ALTER ROLE db_datawriter ADD MEMBER [osdu-delivery-worker-id];") &&
      contains(one([for d in local.sql_setup_databases : d.statements if d.name == "OsduArchive"]), "ALTER ROLE db_datareader ADD MEMBER [osdu-delivery-worker-id];") &&
      contains(one([for d in local.sql_setup_databases : d.statements if d.name == "OsduData"]), "ALTER ROLE db_datawriter ADD MEMBER [osdu-delivery-worker-id];") &&
      contains([for d in local.sql_setup_databases : d.name], "OsduRestored") &&
      !contains([for d in local.sql_setup_databases : d.name], "partner-db-connection")
    )
    error_message = "The worker writes only what a connection with write access names; a database elsewhere is not granted here."
  }
}

run "a_connection_named_as_an_estate_database_is_refused" {
  command = plan

  variables {
    sql_connections = { SQLFLOW_CATALOG_DB = { database = "Other" } }
  }

  expect_failures = [var.sql_connections]
}

run "a_connection_naming_both_a_database_and_a_secret_is_refused" {
  command = plan

  variables {
    sql_connections = { OSDU_DATA_DB = { database = "OsduData", secret_name = "osdu-data" } }
  }

  expect_failures = [var.sql_connections]
}

run "an_admin_whose_object_id_is_not_a_guid_is_refused" {
  command = plan

  variables {
    sql_admins = [{ name = "someone@example.com", object_id = "someone@example.com" }]
  }

  expect_failures = [var.sql_admins]
}

run "password_authentication_keeps_the_sql_login" {
  command = apply

  variables {
    sql_authentication = "password"
    sql_admin_password = "Str0ng'Pass;word!"
  }

  assert {
    condition = (
      strcontains(azurerm_key_vault_secret.catalog_db.value, "User ID=osdudeliveryadmin;Password='Str0ng''Pass;word!';") &&
      strcontains(azurerm_key_vault_secret.connection["OSDU_DATA_DB"].value, "User ID=osdudeliveryadmin;")
    )
    error_message = "The admin password is quoted, with its single quotes doubled, so ADO.NET reads it whole."
  }

  assert {
    condition     = length(azurerm_mssql_server.this[0].azuread_administrator) == 0 && azurerm_mssql_server.this[0].administrator_login == "osdudeliveryadmin"
    error_message = "Under password authentication the server keeps its SQL admin login."
  }

  assert {
    condition     = output.sql_setup_script == "" && length(azurerm_resource_deployment_script_azure_power_shell.grants) == 0
    error_message = "No users need creating when the apps sign in with the SQL login."
  }
}

run "password_authentication_without_a_password_is_refused" {
  command = plan

  variables {
    existing_sql_server = "osdu-mi.abc123def456.database.windows.net"
    sql_authentication  = "password"
  }

  expect_failures = [azurerm_key_vault_secret.catalog_db]
}

run "an_osdu_database_name_an_environment_file_still_sets_is_ignored" {
  command = apply

  variables {
    osdu_database_name = "OsduLedger"
  }

  assert {
    condition = (
      [for d in local.sql_setup_databases : d.name] == ["OsduData", "OsduDelivery"] &&
      local.worker_secret_env == [{ name = "SQLFLOW_CATALOG_DB", secret_name = "osdu-delivery-catalog-db" }]
    )
    error_message = "Nothing is created or granted for it, and every tier reaches the osdu schema in the catalog's database."
  }
}

run "an_externally_managed_registration_is_referenced_not_created" {
  command = apply

  variables {
    provision_entra_app         = false
    azure_ad_allowed_tenant_ids = ["55555555-5555-5555-5555-555555555555"]
    azure_ad_client_id          = "66666666-6666-6666-6666-666666666666"
  }

  assert {
    condition     = length(module.entra_app) == 0 && output.entra_client_id == "66666666-6666-6666-6666-666666666666"
    error_message = "In external mode no registration is created and the given client id is used."
  }

  assert {
    condition     = output.entra_assignment_reminder == ""
    error_message = "The assignment reminder concerns an owned registration only."
  }
}

run "the_partitions_reach_the_control_plane_reading_their_client_secret_from_the_vault" {
  command = apply

  assert {
    condition = (
      local.partition_settings["Osdu__Partitions__dev__Default"] == "true" &&
      local.partition_settings["Osdu__Partitions__dev__Description"] == "The development platform" &&
      local.partition_settings["Osdu__Partitions__dev__Properties__OSDU_URL"] == "https://osdu-dev.example.com" &&
      local.partition_settings["Osdu__Partitions__dev__Properties__OSDU_TOKEN_URL"] == "https://login.example.com/tenant/oauth2/v2.0/token" &&
      local.partition_settings["Osdu__Partitions__dev__Properties__OSDU_SCOPE"] == "https://osdu.example.com/.default" &&
      local.partition_settings["Osdu__Partitions__dev__Properties__OSDU_CLIENT_ID"] == "55555555-5555-5555-5555-555555555555" &&
      local.partition_settings["Osdu__Partitions__dev__Properties__OSDU_LEGAL_TAG"] == "dev-legal" &&
      !contains(keys(local.partition_settings), "Osdu__Partitions__dev__Properties__OSDU_ACL_OWNER")
    )
    error_message = "The one partition is the default, with its values as the settings the control plane reads, and nothing it was not given."
  }

  assert {
    condition     = local.partition_settings["Osdu__Partitions__dev__Properties__OSDU_CLIENT_SECRET"] == "$${keyvault:${azurerm_key_vault.this.name}/osdu-dev-client-secret}"
    error_message = "The control plane is given a reference to the client secret in the vault, never the secret."
  }

  assert {
    condition     = output.osdu_partitions == { dev = { default = true, client_secret_name = "osdu-dev-client-secret" } }
    error_message = "The partitions are reported with their default and the vault secret a person puts the client secret in."
  }
}

run "several_partitions_name_their_default_and_the_vault_secret_each_reads" {
  command = apply

  variables {
    osdu_partitions = {
      "Dev.Main" = {
        url                = "https://osdu-dev.example.com"
        token_url          = "https://login.example.com/token"
        scope              = "https://osdu.example.com/.default"
        client_id          = "55555555-5555-5555-5555-555555555555"
        client_secret_name = "existing-dev-secret"
      }
      prod = {
        default    = true
        url        = "https://osdu.example.com"
        token_url  = "https://login.example.com/token"
        scope      = "https://osdu.example.com/.default"
        client_id  = "66666666-6666-6666-6666-666666666666"
        properties = { OSDU_LOGTYPE_ACL_OWNER = "data.logtypes.owners@prod.example.com", OSDU_SCOPE = "https://override.example.com/.default" }
      }
    }
  }

  assert {
    condition = (
      local.partition_settings["Osdu__Partitions__prod__Default"] == "true" &&
      !contains(keys(local.partition_settings), "Osdu__Partitions__Dev.Main__Default") &&
      local.partition_settings["Osdu__Partitions__Dev.Main__Properties__OSDU_CLIENT_SECRET"] == "$${keyvault:${azurerm_key_vault.this.name}/existing-dev-secret}" &&
      local.partition_settings["Osdu__Partitions__prod__Properties__OSDU_CLIENT_SECRET"] == "$${keyvault:${azurerm_key_vault.this.name}/osdu-prod-client-secret}"
    )
    error_message = "Only the marked partition is the default, and each reads its client secret from the vault secret it names or its default one."
  }

  assert {
    condition = (
      local.partition_settings["Osdu__Partitions__prod__Properties__OSDU_LOGTYPE_ACL_OWNER"] == "data.logtypes.owners@prod.example.com" &&
      local.partition_settings["Osdu__Partitions__prod__Properties__OSDU_SCOPE"] == "https://override.example.com/.default"
    )
    error_message = "Further properties are passed, and one named as a standard value replaces it."
  }
}

run "no_partition_is_refused" {
  command = plan

  variables {
    osdu_partitions = {}
  }

  expect_failures = [var.osdu_partitions]
}

run "several_partitions_without_a_default_are_refused" {
  command = plan

  variables {
    osdu_partitions = {
      dev  = { url = "https://a.example.com", token_url = "https://t.example.com", scope = "s", client_id = "c" }
      test = { url = "https://b.example.com", token_url = "https://t.example.com", scope = "s", client_id = "c" }
    }
  }

  expect_failures = [var.osdu_partitions]
}

run "a_secret_s_value_in_a_partition_property_is_refused" {
  command = plan

  variables {
    osdu_partitions = {
      dev = {
        url        = "https://a.example.com"
        token_url  = "https://t.example.com"
        scope      = "s"
        client_id  = "c"
        properties = { OTHER_CLIENT_SECRET = "a-literal-value" }
      }
    }
  }

  expect_failures = [var.osdu_partitions]
}

run "further_properties_reach_every_run_of_every_partition" {
  command = apply

  variables {
    properties = {
      REGION        = "westeurope"
      SMTP_PASSWORD = "$${keyvault:osdu-kv-test/smtp-password}"
    }
  }

  assert {
    condition = (
      local.control_plane_settings["Osdu__Properties__REGION"] == "westeurope" &&
      local.control_plane_settings["Osdu__Properties__SMTP_PASSWORD"] == "$${keyvault:osdu-kv-test/smtp-password}"
    )
    error_message = "A property set for no partition is a setting every run resolves with, a secret's as a reference."
  }
}

run "a_secret_s_value_in_a_property_is_refused" {
  command = plan

  variables {
    properties = { SMTP_PASSWORD = "a-literal-value" }
  }

  expect_failures = [var.properties]
}

run "the_repositories_are_synced_as_the_configuration_names_them" {
  command = apply

  variables {
    repositories = {
      welldb = {
        remote_url             = "https://github.com/example/welldb-metadata.git"
        branch                 = "release"
        sync_interval_seconds  = 120
        credential_secret_name = "github-welldb-token"
        credential_username    = "x-access-token"
        excluded_flow_paths    = ["flows/legacy/old.yaml", "flows/scratch/draft.yaml"]
      }
      samples = {
        remote_url = "https://github.com/example/public-samples.git"
      }
    }
  }

  assert {
    condition = (
      local.control_plane_settings["ControlPlane__Bootstrap__Repositories__welldb__RemoteUrl"] == "https://github.com/example/welldb-metadata.git" &&
      local.control_plane_settings["ControlPlane__Bootstrap__Repositories__welldb__Branch"] == "release" &&
      local.control_plane_settings["ControlPlane__Bootstrap__Repositories__welldb__SyncIntervalSeconds"] == "120" &&
      local.control_plane_settings["ControlPlane__Bootstrap__Repositories__welldb__CredentialReference"] == "$${keyvault:${azurerm_key_vault.this.name}/github-welldb-token}" &&
      local.control_plane_settings["ControlPlane__Bootstrap__Repositories__welldb__CredentialUsername"] == "x-access-token" &&
      local.control_plane_settings["ControlPlane__Bootstrap__Repositories__welldb__ExcludedFlowPaths__0"] == "flows/legacy/old.yaml" &&
      local.control_plane_settings["ControlPlane__Bootstrap__Repositories__welldb__ExcludedFlowPaths__1"] == "flows/scratch/draft.yaml"
    )
    error_message = "A private repository is synced from its branch with its token read from the vault, never written here."
  }

  assert {
    condition = (
      local.control_plane_settings["ControlPlane__Bootstrap__Repositories__samples__Branch"] == "main" &&
      local.control_plane_settings["ControlPlane__Bootstrap__Repositories__samples__SyncIntervalSeconds"] == "300" &&
      !contains(keys(local.control_plane_settings), "ControlPlane__Bootstrap__Repositories__samples__CredentialReference")
    )
    error_message = "A public repository takes the default branch and interval, and no credential."
  }
}

run "a_github_app_reads_the_estates_github_repositories_that_name_no_token" {
  command = apply

  variables {
    github_app = {
      app_id                  = "Iv23liEstateApp"
      private_key_secret_name = "github-app-private-key"
    }
    repositories = {
      welldb = {
        remote_url = "https://github.com/example/welldb-metadata.git"
      }
      tokened = {
        remote_url             = "https://github.com/example/other-metadata.git"
        credential_secret_name = "github-other-token"
      }
      samples = {
        remote_url     = "https://github.com/example/public-samples.git"
        use_github_app = false
      }
      bitbucket = {
        remote_url = "https://bitbucket.org/example/flows.git"
      }
    }
  }

  assert {
    condition     = local.control_plane_settings["ControlPlane__Bootstrap__Repositories__welldb__CredentialReference"] == "$${githubapp:app=Iv23liEstateApp;key=keyvault:${azurerm_key_vault.this.name}/github-app-private-key}"
    error_message = "A GitHub repository naming no token is read with the estate's App, its key read from the vault: no token is stored."
  }

  assert {
    condition     = local.control_plane_settings["ControlPlane__Bootstrap__Repositories__tokened__CredentialReference"] == "$${keyvault:${azurerm_key_vault.this.name}/github-other-token}"
    error_message = "A repository naming its own token keeps it, App or not."
  }

  assert {
    condition = (
      !contains(keys(local.control_plane_settings), "ControlPlane__Bootstrap__Repositories__samples__CredentialReference") &&
      !contains(keys(local.control_plane_settings), "ControlPlane__Bootstrap__Repositories__bitbucket__CredentialReference")
    )
    error_message = "A repository that opts out of the App, and one on a host the App does not serve, are read with no credential."
  }

  assert {
    condition     = output.github_app_private_key_secret == "github-app-private-key"
    error_message = "The deployment names the vault secret the App's key is put in."
  }

  assert {
    condition     = local.worker_secret_env == [{ name = "SQLFLOW_CATALOG_DB", secret_name = "osdu-delivery-catalog-db" }]
    error_message = "A worker needs nothing new for the App: the credential reference reaches it with each run."
  }
}

run "external_triggers_are_trusted_as_the_configuration_names_them" {
  command = apply

  variables {
    repositories = {
      welldb = {
        remote_url = "https://github.com/example/welldb-metadata.git"
        branch     = "release"
      }
      samples = {
        remote_url = "https://github.com/example/public-samples.git"
      }
    }
    external_triggers = {
      deliver = {
        repository = "welldb"
        schedules  = ["nightly", "hourly"]
        github = {
          repository  = "example/welldb-metadata"
          workflow    = ".github/workflows/sqlflow.yml"
          environment = "production"
        }
      }
      pinned = {
        repository = "samples"
        github = {
          repository    = "example/public-samples"
          repository_id = "123456"
          ref           = "refs/tags/v*"
        }
      }
      scheduler = {
        repository      = "samples"
        description     = "The on-premises scheduler"
        all_schedules   = true
        key_secret_name = "scheduler-trigger-key"
      }
    }
  }

  assert {
    condition = (
      local.control_plane_settings["ControlPlane__Bootstrap__ExternalTriggers__deliver__RepoSource"] == "welldb" &&
      local.control_plane_settings["ControlPlane__Bootstrap__ExternalTriggers__deliver__AllSchedules"] == "false" &&
      local.control_plane_settings["ControlPlane__Bootstrap__ExternalTriggers__deliver__Schedules__0"] == "nightly" &&
      local.control_plane_settings["ControlPlane__Bootstrap__ExternalTriggers__deliver__Schedules__1"] == "hourly" &&
      local.control_plane_settings["ControlPlane__Bootstrap__ExternalTriggers__deliver__GitHub__Repository"] == "example/welldb-metadata" &&
      local.control_plane_settings["ControlPlane__Bootstrap__ExternalTriggers__deliver__GitHub__Ref"] == "refs/heads/release" &&
      local.control_plane_settings["ControlPlane__Bootstrap__ExternalTriggers__deliver__GitHub__Workflow"] == ".github/workflows/sqlflow.yml" &&
      local.control_plane_settings["ControlPlane__Bootstrap__ExternalTriggers__deliver__GitHub__Environment"] == "production" &&
      !contains(keys(local.control_plane_settings), "ControlPlane__Bootstrap__ExternalTriggers__deliver__KeyReference")
    )
    error_message = "A GitHub trigger trusts the workflow it names on the branch its repository syncs, and holds no secret."
  }

  assert {
    condition = (
      local.control_plane_settings["ControlPlane__Bootstrap__ExternalTriggers__pinned__GitHub__RepositoryId"] == "123456" &&
      local.control_plane_settings["ControlPlane__Bootstrap__ExternalTriggers__pinned__GitHub__Ref"] == "refs/tags/v*" &&
      !contains(keys(local.control_plane_settings), "ControlPlane__Bootstrap__ExternalTriggers__pinned__GitHub__Workflow") &&
      length([for name in keys(local.control_plane_settings) : name if startswith(name, "ControlPlane__Bootstrap__ExternalTriggers__pinned__Schedules__")]) == 0
    )
    error_message = "A trigger takes the ref and the repository id it names, and one naming no schedule only syncs."
  }

  assert {
    condition = (
      local.control_plane_settings["ControlPlane__Bootstrap__ExternalTriggers__scheduler__KeyReference"] == "$${keyvault:${azurerm_key_vault.this.name}/scheduler-trigger-key}" &&
      local.control_plane_settings["ControlPlane__Bootstrap__ExternalTriggers__scheduler__AllSchedules"] == "true" &&
      local.control_plane_settings["ControlPlane__Bootstrap__ExternalTriggers__scheduler__Description"] == "The on-premises scheduler" &&
      output.external_trigger_key_secrets == { scheduler = "scheduler-trigger-key" }
    )
    error_message = "A key trigger reads its key from the vault, never from here, and the deployment names the secret to put it in."
  }

  assert {
    condition     = local.control_plane_settings["ControlPlane__ExternalTriggers__GitHub__Audience"] == local.control_plane_url
    error_message = "A GitHub job asks its token for this control plane's own address, so a token minted for another deployment is refused."
  }
}

run "an_external_trigger_of_a_repository_not_configured_is_refused" {
  command = plan

  variables {
    external_triggers = {
      deliver = {
        repository = "welldb"
        github     = { repository = "example/welldb-metadata" }
      }
    }
  }

  expect_failures = [terraform_data.external_triggers]
}

run "an_external_trigger_names_one_proof" {
  command = plan

  variables {
    repositories = {
      welldb = { remote_url = "https://github.com/example/welldb-metadata.git" }
    }
    external_triggers = {
      both = {
        repository      = "welldb"
        github          = { repository = "example/welldb-metadata" }
        key_secret_name = "welldb-trigger-key"
      }
    }
  }

  expect_failures = [var.external_triggers]
}

run "a_github_enterprise_app_reads_its_own_hosts_repositories" {
  command = apply

  variables {
    github_app = {
      app_id                  = "123456"
      private_key_secret_name = "ghes-app-key"
      api_url                 = "https://ghe.example.com/api/v3/"
    }
    repositories = {
      enterprise = { remote_url = "https://ghe.example.com/example/flows.git" }
      public     = { remote_url = "https://github.com/example/flows.git" }
    }
  }

  assert {
    condition     = local.control_plane_settings["ControlPlane__Bootstrap__Repositories__enterprise__CredentialReference"] == "$${githubapp:app=123456;key=keyvault:${azurerm_key_vault.this.name}/ghes-app-key;api=https://ghe.example.com/api/v3}"
    error_message = "A GitHub Enterprise App names its API root, and serves its own host's repositories."
  }

  assert {
    condition     = !contains(keys(local.control_plane_settings), "ControlPlane__Bootstrap__Repositories__public__CredentialReference")
    error_message = "A GitHub Enterprise App does not serve github.com."
  }
}

run "a_github_app_with_a_malformed_app_id_is_refused" {
  command = plan

  variables {
    github_app = { app_id = "my app", private_key_secret_name = "github-app-private-key" }
  }

  expect_failures = [var.github_app]
}

run "a_github_app_key_named_against_the_vaults_rules_is_refused" {
  command = plan

  variables {
    github_app = { app_id = "Iv23liEstateApp", private_key_secret_name = "github_app/key" }
  }

  expect_failures = [var.github_app]
}

run "a_github_app_api_over_plain_http_is_refused" {
  command = plan

  variables {
    github_app = { app_id = "Iv23liEstateApp", private_key_secret_name = "github-app-private-key", api_url = "http://ghe.example.com/api/v3" }
  }

  expect_failures = [var.github_app]
}

run "a_repository_over_plain_http_is_refused" {
  command = plan

  variables {
    repositories = { welldb = { remote_url = "http://git.example.com/welldb.git" } }
  }

  expect_failures = [var.repositories]
}

run "the_landing_storage_is_created_granted_and_given_to_every_run" {
  command = apply

  variables {
    landing_storage = {}
  }

  assert {
    condition = (
      length(azurerm_storage_account.landing) == 1 &&
      azurerm_storage_account.landing[0].name == "osdulanding${substr(local.suffix, 0, 13)}" &&
      length(azurerm_storage_account.landing[0].name) <= 24 &&
      azurerm_storage_account.landing[0].is_hns_enabled &&
      !azurerm_storage_account.landing[0].shared_access_key_enabled &&
      !azurerm_storage_account.landing[0].allow_nested_items_to_be_public &&
      azurerm_storage_container.landing[0].name == "landing"
    )
    error_message = "The account is created with a hierarchical namespace, reached by identity alone, with its private container."
  }

  assert {
    condition = (
      azurerm_role_assignment.landing_worker[0].role_definition_name == "Storage Blob Data Contributor" &&
      azurerm_role_assignment.landing_worker[0].scope == azurerm_storage_account.landing[0].id &&
      azurerm_role_assignment.landing_control_plane[0].role_definition_name == "Storage Blob Data Reader"
    )
    error_message = "The worker may read and write the landing files, the control plane read them."
  }

  assert {
    condition = (
      output.landing_url == "abfss://landing@${azurerm_storage_account.landing[0].name}.dfs.core.windows.net" &&
      local.control_plane_settings["Osdu__Properties__LANDING_URL"] == output.landing_url
    )
    error_message = "Every run is given the landing location as LANDING_URL."
  }
}

run "an_adopted_landing_account_is_granted_not_created" {
  command = apply

  variables {
    landing_storage = {
      existing_account_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-shared/providers/Microsoft.Storage/storageAccounts/osdusharedfiles"
      container           = "raw"
      reference_name      = "RAW_FILES_URL"
    }
  }

  assert {
    condition     = length(azurerm_storage_account.landing) == 0 && length(azurerm_storage_container.landing) == 0
    error_message = "An adopted account and its container are not created here."
  }

  assert {
    condition = (
      azurerm_role_assignment.landing_worker[0].scope == "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/osdu-shared/providers/Microsoft.Storage/storageAccounts/osdusharedfiles" &&
      local.control_plane_settings["Osdu__Properties__RAW_FILES_URL"] == "abfss://raw@osdusharedfiles.dfs.core.windows.net" &&
      !contains(keys(local.control_plane_settings), "Osdu__Properties__LANDING_URL")
    )
    error_message = "The adopted account is granted and given to every run under the name chosen."
  }
}

run "a_landing_container_azure_refuses_is_refused" {
  command = plan

  variables {
    landing_storage = { container = "Landing_Files" }
  }

  expect_failures = [var.landing_storage]
}

run "the_images_are_the_repository_s_at_the_image_tag_in_the_estate_s_registry" {
  command = apply

  variables {
    acr_name            = "osdutest"
    image_tag           = "abc1234"
    control_plane_image = ""
    worker_image        = ""
    gui_image           = ""
    mcp_enabled         = true
  }

  assert {
    condition = (
      local.control_plane_image == "osdutest.azurecr.io/osdu-delivery-control-plane:abc1234" &&
      local.worker_image == "osdutest.azurecr.io/osdu-delivery-worker:abc1234" &&
      local.gui_image == "osdutest.azurecr.io/osdu-delivery-gui:abc1234" &&
      local.mcp_image == "osdutest.azurecr.io/osdu-delivery-mcp:abc1234"
    )
    error_message = "An app whose image is not named takes the repository's image at the tag, in the estate's registry."
  }

  assert {
    condition     = length(module.mcp) == 1 && output.mcp_url != ""
    error_message = "mcp_enabled deploys the MCP server without naming its image."
  }
}

run "an_image_named_by_neither_its_variable_nor_a_tag_is_refused" {
  command = plan

  variables {
    acr_name            = "osdutest"
    control_plane_image = ""
  }

  expect_failures = [terraform_data.images]
}
