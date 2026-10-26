# The full OSDU Delivery estate on Azure Container Apps in one resource group: Log Analytics and the Container Apps
# environment, a Key Vault holding every secret, the Azure SQL databases the estate has (OsduDelivery, the metadata:
# SQLFlow's catalog and the osdu schema; and each of sql_connections, the data the flows land, load and deliver from),
# a landing storage account for the files the flows read, and the apps composed from the modules beside this file:
#
#   modules/gui            the SPA, external ingress; calls the control plane cross-origin
#   modules/control-plane  the API (in-process worker OFF: the API replica does API work only), CORS'd to the GUI
#   modules/worker         one worker pool, no ingress, scaled 0..N on the control plane's replica target
#   modules/mcp            optional (mcp_image): the MCP server, external ingress, the chat assistant's tool source
#   modules/ai-foundry     optional (ai_foundry_name): an AI Foundry account, project and model for the assistant
#   modules/entra-app      optional (provision_entra_app): the Entra app registration users sign in with
#
# With both the MCP server and a Foundry model, the GUI chat assistant turns on by itself: the control plane calls the
# model keylessly with its own identity, and the model calls the MCP server with a token the control plane delegates
# to that one run.
#
# The configuration is the only description of the estate (osdu/docs/reference/guides/deployment.md). This file creates
# the resources and the identities, and gives the control plane its configuration: the partitions, the values every run
# resolves with (the SQL connections, the landing storage, any further property), the repositories to sync, and the node
# credential. The control plane applies it at every start (its start-up pass, GET /api/v1/bootstrap) and hands every run
# its values, so a worker carries its identity and nothing else.
#
# Secrets are never written here, so none sits in Terraform state: the deployment's setup identity generates the ones
# nobody provides (the JWT signing key, the initial admin's password, the node credential) into the vault when they are
# missing (osdu/deploy/setup/Setup-Estate.ps1), and the ones only a person has (an OSDU client secret, a git token) are
# put in the vault by hand and named here; the control plane reports one it cannot read as a failed start-up step. The
# same setup identity administers the SQL server created here and creates the apps' database users inside every
# deployment, so a deployment needs no step by hand. The vault uses RBAC: the deploying identity needs Key Vault Secrets
# Officer (or Administrator) on it and the right to create role assignments; the deployment scripts need Contributor on
# the resource group (a container instance and a storage account run each one).

data "azurerm_client_config" "current" {}

data "azurerm_resource_group" "this" {
  name = var.resource_group_name
}

locals {
  location = coalesce(var.location, data.azurerm_resource_group.this.location)

  # Stable per resource group, as Bicep's uniqueString(resourceGroup().id) is (the values differ).
  suffix          = substr(sha256(data.azurerm_resource_group.this.id), 0, 13)
  key_vault_name  = coalesce(var.key_vault_name, "osdu-kv-${local.suffix}")
  sql_server_name = coalesce(var.sql_server_name, "osdu-sql-${local.suffix}")

  create_sql          = var.existing_sql_server == ""
  sql_admin_password  = var.sql_admin_password == null ? "" : var.sql_admin_password
  has_sql_password    = nonsensitive(local.sql_admin_password != "")
  sql_entra_auth      = var.sql_authentication == "entra"
  grants_here         = local.sql_entra_auth && local.create_sql
  mcp_enabled         = var.mcp_enabled || var.mcp_image != ""
  ai_foundry_enabled  = var.ai_foundry_name != ""
  chat_assistant_on   = local.mcp_enabled && local.ai_foundry_enabled && var.ai_foundry_model_name != ""
  ai_foundry_endpoint = local.ai_foundry_enabled ? "https://${var.ai_foundry_name}.services.ai.azure.com/api/projects/${var.ai_foundry_project_name}" : ""

  # Secret names shared with the modules (their defaults match these).
  catalog_connection_secret_name = "osdu-delivery-catalog-db"
  jwt_signing_key_secret_name    = "osdu-delivery-jwt-signing-key"
  admin_password_secret_name     = "osdu-delivery-admin-password"
  node_token_secret_name         = "osdu-delivery-node-token"

  # A reference every run resolves on its node with the node's own identity.
  vault_reference = { for name in [local.catalog_connection_secret_name, local.node_token_secret_name] : name => "$${keyvault:${local.key_vault_name}/${name}}" }
}

resource "azurerm_log_analytics_workspace" "this" {
  name                = var.log_analytics_name
  location            = local.location
  resource_group_name = data.azurerm_resource_group.this.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = var.tags
}

resource "azurerm_container_app_environment" "this" {
  name                       = var.environment_name
  location                   = local.location
  resource_group_name        = data.azurerm_resource_group.this.name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.this.id
  infrastructure_subnet_id   = var.infrastructure_subnet_id == "" ? null : var.infrastructure_subnet_id
  tags                       = var.tags

  # Azure creates every Container Apps environment with workload profiles and this built-in serverless one, which the
  # apps run on; declared, so a plan does not try to take it away.
  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }
}

resource "azurerm_key_vault" "this" {
  name                       = local.key_vault_name
  location                   = local.location
  resource_group_name        = data.azurerm_resource_group.this.name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  tags                       = var.tags
}

# ------------------------------------------------------------------------------------------------ setup identity ---

# The identity the deployment's own steps run as (osdu/deploy/setup/Setup-Estate.ps1): it generates the secrets nobody
# provides into the vault, and, as the Entra administrator of the SQL server created here, creates the apps' database
# users. No app runs as it.
resource "azurerm_user_assigned_identity" "setup" {
  name                = var.setup_identity_name
  location            = local.location
  resource_group_name = data.azurerm_resource_group.this.name
  tags                = var.tags
}

resource "azurerm_role_assignment" "setup_secrets_officer" {
  scope                = azurerm_key_vault.this.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = azurerm_user_assigned_identity.setup.principal_id
  principal_type       = "ServicePrincipal"
}

# The secrets the deployment generates, when the vault does not hold them yet; one it holds is never changed, so a value
# rotated by hand stays. Run before the apps, which read them when they are created.
resource "azurerm_resource_deployment_script_azure_power_shell" "secrets" {
  name                = "osdu-delivery-setup-secrets"
  resource_group_name = data.azurerm_resource_group.this.name
  location            = local.location
  version             = "11.0"
  retention_interval  = "P1D"
  cleanup_preference  = "OnSuccess"
  timeout             = "PT30M"
  command_line        = "-Step Secrets"
  script_content      = file("${path.module}/../setup/Setup-Estate.ps1")
  # Runs at every apply, so a secret deleted from the vault is generated again.
  force_update_tag = plantimestamp()
  tags             = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.setup.id]
  }

  environment_variable {
    name = "SETUP_SPEC"
    value = jsonencode({
      vault = local.key_vault_name
      generated = [
        { name = local.jwt_signing_key_secret_name, kind = "signing-key" },
        { name = local.admin_password_secret_name, kind = "password" },
        { name = local.node_token_secret_name, kind = "node-token" },
      ]
      sql = null
    })
  }

  depends_on = [azurerm_role_assignment.setup_secrets_officer]
}

# ---------------------------------------------------------------------------------------------------------- SQL ---

# Under Entra authentication (the default) the server is administered by the estate's setup identity and has no SQL
# logins at all; under password authentication, the SQL admin login the connection strings carry.
resource "azurerm_mssql_server" "this" {
  count = local.create_sql ? 1 : 0

  name                          = local.sql_server_name
  location                      = local.location
  resource_group_name           = data.azurerm_resource_group.this.name
  version                       = "12.0"
  administrator_login           = local.sql_entra_auth ? null : var.sql_admin_login
  administrator_login_password  = local.sql_entra_auth ? null : local.sql_admin_password
  minimum_tls_version           = "1.2"
  public_network_access_enabled = true
  tags                          = var.tags

  dynamic "azuread_administrator" {
    for_each = local.sql_entra_auth ? [1] : []
    content {
      # An application administers the server by its client id (az sql server create: "Object Id for User or Group,
      # Client Id for Applications"), the id its tokens carry.
      login_username              = azurerm_user_assigned_identity.setup.name
      object_id                   = azurerm_user_assigned_identity.setup.client_id
      azuread_authentication_only = true
    }
  }

  lifecycle {
    precondition {
      condition     = local.sql_entra_auth || local.has_sql_password
      error_message = "A SQL server created here under password authentication needs sql_admin_password; or use the default Entra authentication, administered by the estate's setup identity."
    }
  }
}

# 0.0.0.0 is Azure's "allow Azure services" switch: the Container Apps consumption plan and the deployment scripts have
# no fixed outbound address for a narrower rule. To close the databases to the public internet, run the environment in
# a VNet and reach SQL over a private endpoint instead (then disable public access and drop this rule).
resource "azurerm_mssql_firewall_rule" "allow_azure_services" {
  count = local.create_sql ? 1 : 0

  name             = "AllowAllWindowsAzureIps"
  server_id        = azurerm_mssql_server.this[0].id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_mssql_database" "catalog" {
  count = local.create_sql ? 1 : 0

  name      = var.catalog_database_name
  server_id = azurerm_mssql_server.this[0].id
  sku_name  = var.sql_database_sku
  collation = "SQL_Latin1_General_CP1_CI_AS"
  tags      = var.tags
}

# The delivery module's own database, which an estate could once keep apart from the catalog's. The osdu schema lives in
# the catalog's database (osdu_database_name is no longer read), so a state still holding one forgets it rather than
# destroying it: it holds a ledger, which belongs in the catalog's database.
removed {
  from = azurerm_mssql_database.osdu

  lifecycle {
    destroy = false
  }
}

locals {
  # The connections on the estate's server, and the databases this configuration creates for them: one per database
  # name, whichever connections name it, and never the catalog's, which holds the osdu schema too.
  server_connections = { for name, c in var.sql_connections : name => c if c.database != null }
  created_databases = toset([
    for name, c in local.server_connections : c.database
    if local.create_sql && c.create && c.database != var.catalog_database_name
  ])
}

# The data databases the flows work in: the pre-ingestion flows land the source files there, the ingestion flows load
# the keyed tables, and the delivery flows read them (each in a schema of its own). Created only with the server; on an
# existing server they are provisioned out of band.
resource "azurerm_mssql_database" "connection" {
  for_each = local.created_databases

  name      = each.value
  server_id = azurerm_mssql_server.this[0].id
  sku_name  = var.sql_connection_sku
  collation = "SQL_Latin1_General_CP1_CI_AS"
  tags      = var.tags
}

moved {
  from = azurerm_mssql_database.data[0]
  to   = azurerm_mssql_database.connection["OsduData"]
}

locals {
  # host,port for the connection strings: the created server on the standard port, or the existing address with its
  # own port when it carries one (a Managed Instance public endpoint is host,3342).
  sql_server_address = local.create_sql ? "${azurerm_mssql_server.this[0].fully_qualified_domain_name},1433" : (
    strcontains(var.existing_sql_server, ",") ? var.existing_sql_server : "${var.existing_sql_server},1433"
  )
  sql_server_host = local.create_sql ? azurerm_mssql_server.this[0].fully_qualified_domain_name : split(",", var.existing_sql_server)[0]

  # The one place each connection string exists; everything else references the vault secret. With password
  # authentication the SQL admin login signs in, its password quoted (embedded single quotes doubled) so any complex
  # value survives ADO.NET parsing. With Entra authentication each app signs in as its own managed identity (the
  # AZURE_CLIENT_ID it is given), so no connection carries a credential.
  quoted_sql_admin_password = "'${replace(local.sql_admin_password, "'", "''")}'"
  sql_credentials           = local.sql_entra_auth ? "Authentication=Active Directory Default" : "User ID=${var.sql_admin_login};Password=${local.quoted_sql_admin_password}"
  connection_string_tail    = "${local.sql_credentials};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30"
  catalog_connection        = "Server=tcp:${local.sql_server_address};Initial Catalog=${var.catalog_database_name};${local.connection_string_tail}"

  # Each connection's vault secret: built here for one on the estate's server, put there by hand for one elsewhere.
  connection_secret_names = {
    for name, c in var.sql_connections : name => coalesce(c.secret_name, "osdu-delivery-conn-${replace(lower(name), "_", "-")}")
  }
  connection_strings = {
    for name, c in local.server_connections : name => "Server=tcp:${local.sql_server_address};Initial Catalog=${c.database};${local.connection_string_tail}"
  }
}

# Under Entra authentication, the users each database needs for the app identities and the people sql_admins names, and
# the roles each holds. The control plane owns the catalog and the osdu schema (it migrates both) and reads every data
# database; the worker reaches the osdu schema only (and creates the dimension tables and views there), and creates,
# loads and reads the flows' tables in each data database it writes. The osdu schema is created first if missing, so the
# worker's grants hold before the control plane's first migration; the migration creates it only when it is missing.
# The estate's setup identity runs them inside every deployment (the grants step of osdu/deploy/setup/Setup-Estate.ps1);
# with an existing server, an Entra administrator of that server runs the sql_setup_script output instead.
locals {
  control_plane_user = { name = module.control_plane.identity_name, id = module.control_plane.identity_client_id, type = "E" }
  worker_user        = { name = module.worker.identity_name, id = module.worker.identity_client_id, type = "E" }
  admin_users        = [for a in var.sql_admins : { name = a.name, id = a.object_id, type = a.type == "group" ? "X" : "E" }]

  admins_own = [for a in var.sql_admins : "ALTER ROLE db_owner ADD MEMBER [${replace(a.name, "]", "]]")}];"]
  control_plane_owns = [
    "ALTER ROLE db_owner ADD MEMBER [${local.control_plane_user.name}];",
  ]
  worker_reaches_osdu_schema = [
    "IF SCHEMA_ID(N'osdu') IS NULL EXEC(N'CREATE SCHEMA [osdu];');",
    "GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE, ALTER, REFERENCES ON SCHEMA::osdu TO [${local.worker_user.name}];",
    "GRANT CREATE TABLE, CREATE VIEW TO [${local.worker_user.name}];",
  ]
  control_plane_reads = [
    "ALTER ROLE db_datareader ADD MEMBER [${local.control_plane_user.name}];",
  ]
  worker_writes = [
    "ALTER ROLE db_ddladmin ADD MEMBER [${local.worker_user.name}];",
    "ALTER ROLE db_datareader ADD MEMBER [${local.worker_user.name}];",
    "ALTER ROLE db_datawriter ADD MEMBER [${local.worker_user.name}];",
  ]
  worker_reads = [
    "ALTER ROLE db_datareader ADD MEMBER [${local.worker_user.name}];",
  ]

  sql_setup_entries = concat(
    [{
      name       = var.catalog_database_name
      purpose    = "the catalog, and the osdu schema beside it"
      users      = concat([local.control_plane_user, local.worker_user], local.admin_users)
      statements = concat(local.control_plane_owns, local.worker_reaches_osdu_schema, local.admins_own)
    }],
    [for name, c in local.server_connections : {
      name       = c.database
      purpose    = "${name}, the data the flows ${c.access == "write" ? "land, load and deliver from" : "read"}"
      users      = concat([local.control_plane_user, local.worker_user], local.admin_users)
      statements = concat(local.control_plane_reads, c.access == "write" ? local.worker_writes : local.worker_reads, local.admins_own)
    }],
  )

  # One part per database, however many roles it plays.
  sql_setup_grouped = { for entry in local.sql_setup_entries : entry.name => entry... }
  sql_setup_databases = [for name, entries in local.sql_setup_grouped : {
    name       = name
    purpose    = join("; ", distinct([for entry in entries : entry.purpose]))
    users      = distinct(flatten([for entry in entries : entry.users]))
    statements = distinct(flatten([for entry in entries : entry.statements]))
  }]

  # For a server this configuration does not administer: the same users and roles, for its Entra administrator to run.
  sql_setup_script = local.sql_entra_auth && !local.create_sql ? templatefile("${path.module}/sql-setup.sql.tftpl", {
    server    = var.existing_sql_server
    databases = local.sql_setup_databases
  }) : ""
}

# The grants, inside every deployment, once the apps' identities exist: an app that starts before them retries its first
# connection until they hold.
resource "azurerm_resource_deployment_script_azure_power_shell" "grants" {
  count = local.grants_here ? 1 : 0

  name                = "osdu-delivery-setup-grants"
  resource_group_name = data.azurerm_resource_group.this.name
  location            = local.location
  version             = "11.0"
  retention_interval  = "P1D"
  cleanup_preference  = "OnSuccess"
  timeout             = "PT30M"
  command_line        = "-Step Grants"
  script_content      = file("${path.module}/../setup/Setup-Estate.ps1")
  # Runs at every apply, so the grants are always as this configuration says.
  force_update_tag = plantimestamp()
  tags             = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.setup.id]
  }

  environment_variable {
    name = "SETUP_SPEC"
    value = jsonencode({
      vault     = local.key_vault_name
      generated = []
      sql = {
        server    = local.sql_server_host
        databases = [for d in local.sql_setup_databases : { name = d.name, users = d.users, statements = d.statements }]
      }
    })
  }

  depends_on = [
    azurerm_mssql_database.catalog,
    azurerm_mssql_database.connection,
    azurerm_mssql_firewall_rule.allow_azure_services,
  ]
}

# ------------------------------------------------------------------------------------------------- partitions ---

# The OSDU partitions, as the control plane's configuration names them (Osdu:Partitions): it registers them when it starts,
# with the values their runs resolve. Each client secret is put in this estate's vault by hand and travels as a reference
# to it, which the control plane and the nodes resolve with their own identities when a run needs it.
locals {
  partition_secret_names = {
    for id, p in var.osdu_partitions : id => coalesce(p.client_secret_name, "osdu-${replace(lower(id), "/[^a-z0-9]/", "-")}-client-secret")
  }
  partition_properties = {
    for id, p in var.osdu_partitions : id => merge(
      {
        OSDU_URL           = p.url
        OSDU_TOKEN_URL     = p.token_url
        OSDU_SCOPE         = p.scope
        OSDU_CLIENT_ID     = p.client_id
        OSDU_CLIENT_SECRET = "$${keyvault:${local.key_vault_name}/${local.partition_secret_names[id]}}"
      },
      p.acl_owner == null ? {} : { OSDU_ACL_OWNER = p.acl_owner },
      p.acl_viewer == null ? {} : { OSDU_ACL_VIEWER = p.acl_viewer },
      p.legal_tag == null ? {} : { OSDU_LEGAL_TAG = p.legal_tag },
      p.properties,
    )
  }

  # The settings the control plane reads them from, one environment variable per value. One partition alone is the
  # default without saying so.
  partition_settings = merge(
    { for id, p in var.osdu_partitions : "Osdu__Partitions__${id}__Default" => "true" if p.default || length(var.osdu_partitions) == 1 },
    { for id, p in var.osdu_partitions : "Osdu__Partitions__${id}__Description" => trimspace(p.description) if p.description != null ? trimspace(p.description) != "" : false },
    merge([for id, properties in local.partition_properties : { for name, value in properties : "Osdu__Partitions__${id}__Properties__${name}" => value }]...),
  )
}

# Each external trigger syncs one of the repositories the configuration names, which a variable's own check cannot see.
resource "terraform_data" "external_triggers" {
  lifecycle {
    precondition {
      condition     = alltrue([for t in values(var.external_triggers) : contains(keys(var.repositories), t.repository)])
      error_message = "An external trigger syncs one of repositories, named by its key. Not so: ${join(", ", [for name, t in var.external_triggers : name if !contains(keys(var.repositories), t.repository)])}."
    }
  }
}

# An app whose image variable is empty takes this repository's image at image_tag, which needs both.
resource "terraform_data" "images" {
  lifecycle {
    precondition {
      condition     = !local.images_from_tag || (var.image_tag != "" && local.acr_login_server != "")
      error_message = "An app's image is named neither by its own variable nor by image_tag in a registry: set image_tag (deploy.ps1 -Infrastructure passes the commit) and acr_name or acr_login_server, or name every app's image."
    }
  }
}

# ------------------------------------------------------------------------------------------------------ secrets ---

# The connection strings, which carry no credential under Entra authentication: derived from the server and the
# databases, so this configuration writes them. The catalog's is the module's too: the osdu schema lives in its database.
resource "azurerm_key_vault_secret" "catalog_db" {
  name         = local.catalog_connection_secret_name
  value        = local.catalog_connection
  key_vault_id = azurerm_key_vault.this.id

  lifecycle {
    precondition {
      condition     = local.sql_entra_auth || local.has_sql_password
      error_message = "sql_admin_password is required with sql_authentication = \"password\"; set it, or use the default Entra authentication, where each app signs in as its own managed identity."
    }
  }
}

resource "azurerm_key_vault_secret" "connection" {
  for_each = local.connection_strings

  name         = local.connection_secret_names[each.key]
  value        = each.value
  key_vault_id = azurerm_key_vault.this.id
}

moved {
  from = azurerm_key_vault_secret.data_db
  to   = azurerm_key_vault_secret.connection["OSDU_DATA_DB"]
}

# The secrets an earlier version of this configuration wrote from variables now live in the vault alone: generated by the
# setup step when missing, or put there by hand. Forgetting them here keeps them in the vault.
removed {
  from = azurerm_key_vault_secret.jwt_signing_key
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_key_vault_secret.admin_password
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_key_vault_secret.node_token
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_key_vault_secret.git_token
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_key_vault_secret.osdu_client_secret
  lifecycle {
    destroy = false
  }
}

removed {
  from = terraform_data.osdu_partitions
  lifecycle {
    destroy = false
  }
}

# ------------------------------------------------------------------------------------------------ landing storage ---

# The storage account the flows read their source files from, created here or adopted: the worker's identity may read
# and write it (a flow that moves a file it has read), the control plane's read it (a person's preview of a flow).
locals {
  landing_created     = var.landing_storage != null ? var.landing_storage.existing_account_id == null : false
  landing_account_id  = var.landing_storage == null ? "" : (local.landing_created ? azurerm_storage_account.landing[0].id : var.landing_storage.existing_account_id)
  landing_account     = var.landing_storage == null ? "" : (local.landing_created ? azurerm_storage_account.landing[0].name : element(split("/", var.landing_storage.existing_account_id), 8))
  landing_url         = var.landing_storage == null ? "" : "abfss://${var.landing_storage.container}@${local.landing_account}.dfs.core.windows.net"
  landing_settings    = var.landing_storage == null ? {} : { "Osdu__Properties__${var.landing_storage.reference_name}" = local.landing_url }
  landing_account_raw = "osdulanding${substr(local.suffix, 0, 13)}"
}

resource "azurerm_storage_account" "landing" {
  count = local.landing_created ? 1 : 0

  name                            = coalesce(var.landing_storage.account_name, local.landing_account_raw)
  resource_group_name             = data.azurerm_resource_group.this.name
  location                        = local.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  account_kind                    = "StorageV2"
  is_hns_enabled                  = true
  min_tls_version                 = "TLS1_2"
  shared_access_key_enabled       = false
  allow_nested_items_to_be_public = false
  default_to_oauth_authentication = true
  tags                            = var.tags
}

resource "azurerm_storage_container" "landing" {
  count = local.landing_created ? 1 : 0

  name                  = var.landing_storage.container
  storage_account_id    = azurerm_storage_account.landing[0].id
  container_access_type = "private"
}

resource "azurerm_role_assignment" "landing_worker" {
  count = var.landing_storage == null ? 0 : 1

  scope                = local.landing_account_id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = module.worker.identity_principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "landing_control_plane" {
  count = var.landing_storage == null ? 0 : 1

  scope                = local.landing_account_id
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = module.control_plane.identity_principal_id
  principal_type       = "ServicePrincipal"
}

# --------------------------------------------------------------------------------------------------------- apps ---

# The registry the images are pulled from, read once here: a module reading it would defer the read, and every id
# derived from it, whenever anything it depends on changes.
data "azurerm_container_registry" "this" {
  count = var.acr_name != "" ? 1 : 0

  name                = var.acr_name
  resource_group_name = data.azurerm_resource_group.this.name
}

locals {
  acr_id           = var.acr_name != "" ? data.azurerm_container_registry.this[0].id : ""
  acr_login_server = var.acr_name != "" ? data.azurerm_container_registry.this[0].login_server : var.acr_login_server

  # Each app's image: its own variable, or this repository's image of it at image_tag in the estate's registry.
  repository_images = {
    for app in ["control-plane", "worker", "gui", "mcp"] : app => "${local.acr_login_server}/osdu-delivery-${app}:${var.image_tag}"
  }
  control_plane_image = var.control_plane_image != "" ? var.control_plane_image : local.repository_images["control-plane"]
  worker_image        = var.worker_image != "" ? var.worker_image : local.repository_images["worker"]
  gui_image           = var.gui_image != "" ? var.gui_image : local.repository_images["gui"]
  mcp_image           = var.mcp_image != "" ? var.mcp_image : local.repository_images["mcp"]
  images_from_tag = (
    var.control_plane_image == "" || var.worker_image == "" || var.gui_image == "" ||
    (local.mcp_enabled && var.mcp_image == "")
  )

  # Each app's FQDN is <app>.<environment default domain>, so the cross-origin wiring (GUI -> API base URL, control
  # plane -> CORS origin, MCP server -> its own host) is computed up front instead of creating a cycle between apps.
  default_domain    = azurerm_container_app_environment.this.default_domain
  control_plane_url = "https://${var.control_plane_name}.${local.default_domain}"
  gui_url           = "https://${var.gui_name}.${local.default_domain}"
  mcp_fqdn          = "${var.mcp_name}.${local.default_domain}"

  # The registration's own tenant is always trusted when it is provisioned here; in external mode the full allow-list
  # comes from the caller. An empty client id leaves SSO off (local sign-in only).
  entra_sign_in_audience = length(var.azure_ad_additional_allowed_tenant_ids) == 0 ? "AzureADMyOrg" : "AzureADMultipleOrgs"
  effective_tenant_ids   = var.provision_entra_app ? concat([data.azurerm_client_config.current.tenant_id], var.azure_ad_additional_allowed_tenant_ids) : var.azure_ad_allowed_tenant_ids
  effective_entra_client = var.provision_entra_app ? module.entra_app[0].client_id : var.azure_ad_client_id

  # The values set for no partition every run of every kind resolves with (Osdu:Properties): the metadata database under
  # the name the flows read the osdu schema's views by, each SQL connection, the landing storage, and the further
  # properties. A connection is a vault reference a node resolves with its own identity.
  property_settings = merge(
    { "Osdu__Properties__SQLFLOW_CATALOG_DB" = local.vault_reference[local.catalog_connection_secret_name] },
    { for name, secret in local.connection_secret_names : "Osdu__Properties__${name}" => "$${keyvault:${local.key_vault_name}/${secret}}" },
    local.landing_settings,
    { for name, value in var.properties : "Osdu__Properties__${name}" => value },
  )

  # The estate's GitHub App (var.github_app) as the credential reference of a repository on its GitHub: a sync, and a
  # worker checking out the commit a run is pinned to, each mint a token for that repository alone, reading the App's key
  # from the vault with their own identity (sqlflow/docs/reference/guides/github-app.md).
  github_app_api_root = var.github_app == null ? null : (var.github_app.api_url == null ? "https://api.github.com" : trimsuffix(var.github_app.api_url, "/"))
  github_app_web_host = local.github_app_api_root == null ? null : trimprefix(lower(regex("^https://([^/]+)", local.github_app_api_root)[0]), "api.")
  github_app_reference = var.github_app == null ? null : join("", [
    "$${githubapp:app=", var.github_app.app_id,
    ";key=keyvault:", local.key_vault_name, "/", var.github_app.private_key_secret_name,
    var.github_app.api_url == null ? "" : ";api=${local.github_app_api_root}",
    "}",
  ])

  # Each repository's credential: its own token when it names one, else the App when the repository is on the App's
  # GitHub and leaves use_github_app on, else none (a public remote).
  repository_credential = { for name, r in var.repositories : name => (
    r.credential_secret_name != null ? "$${keyvault:${local.key_vault_name}/${r.credential_secret_name}}" : (
      local.github_app_reference == null ? null : (
        r.use_github_app && contains([local.github_app_web_host, "www.${local.github_app_web_host}"], lower(regex("^https://(?:[^@/]+@)?([^/:]+)", r.remote_url)[0]))
        ? local.github_app_reference
        : null
      )
    )
  ) }

  # The repositories the control plane syncs, written at its start and kept as configured.
  repository_settings = merge([for name, r in var.repositories : merge(
    {
      "ControlPlane__Bootstrap__Repositories__${name}__RemoteUrl"           = r.remote_url
      "ControlPlane__Bootstrap__Repositories__${name}__Branch"              = r.branch
      "ControlPlane__Bootstrap__Repositories__${name}__SyncIntervalSeconds" = tostring(r.sync_interval_seconds)
    },
    local.repository_credential[name] == null ? {} : { "ControlPlane__Bootstrap__Repositories__${name}__CredentialReference" = local.repository_credential[name] },
    r.credential_username == null ? {} : { "ControlPlane__Bootstrap__Repositories__${name}__CredentialUsername" = r.credential_username },
    { for i, path in r.excluded_flow_paths : "ControlPlane__Bootstrap__Repositories__${name}__ExcludedFlowPaths__${i}" => path },
  )]...)

  # The external triggers the control plane trusts, written at its start, and the audience a GitHub job asks its token
  # for: the control plane's own address, so a token minted for another deployment is refused here.
  external_trigger_settings = merge(
    { "ControlPlane__ExternalTriggers__GitHub__Audience" = local.control_plane_url },
    [for name, t in var.external_triggers : merge(
      {
        "ControlPlane__Bootstrap__ExternalTriggers__${name}__RepoSource"   = t.repository
        "ControlPlane__Bootstrap__ExternalTriggers__${name}__AllSchedules" = tostring(t.all_schedules)
      },
      t.description == null ? tomap({}) : tomap({ "ControlPlane__Bootstrap__ExternalTriggers__${name}__Description" = t.description }),
      { for i, schedule in t.schedules : "ControlPlane__Bootstrap__ExternalTriggers__${name}__Schedules__${i}" => schedule },
      t.github == null ? tomap({ "ControlPlane__Bootstrap__ExternalTriggers__${name}__KeyReference" = "$${keyvault:${local.key_vault_name}/${t.key_secret_name}}" }) : merge(
        {
          "ControlPlane__Bootstrap__ExternalTriggers__${name}__GitHub__Repository" = t.github.repository
          "ControlPlane__Bootstrap__ExternalTriggers__${name}__GitHub__Ref"        = coalesce(t.github.ref, "refs/heads/${try(var.repositories[t.repository].branch, "main")}")
        },
        t.github.repository_id == null ? tomap({}) : tomap({ "ControlPlane__Bootstrap__ExternalTriggers__${name}__GitHub__RepositoryId" = t.github.repository_id }),
        t.github.workflow == null ? tomap({}) : tomap({ "ControlPlane__Bootstrap__ExternalTriggers__${name}__GitHub__Workflow" = t.github.workflow }),
        t.github.environment == null ? tomap({}) : tomap({ "ControlPlane__Bootstrap__ExternalTriggers__${name}__GitHub__Environment" = t.github.environment }),
      ),
    )]...
  )

  control_plane_settings = merge(
    local.partition_settings,
    local.property_settings,
    local.repository_settings,
    local.external_trigger_settings,
    # The node credential the workers present, which the control plane makes the admin's one node-scope token at start.
    { "ControlPlane__Bootstrap__NodeTokenReference" = local.vault_reference[local.node_token_secret_name] },
  )

  # A node holds its identity and the catalog's connection alone, which it reads and writes the ledger in the osdu schema
  # through: everything its runs read is handed to each run.
  worker_secret_env = [
    { name = "SQLFLOW_CATALOG_DB", secret_name = local.catalog_connection_secret_name },
  ]
}

module "entra_app" {
  source = "./modules/entra-app"
  count  = var.provision_entra_app ? 1 : 0

  display_name            = var.entra_app_display_name
  unique_name             = var.entra_app_unique_name
  redirect_uri            = local.gui_url
  allowed_group_object_id = var.azure_ad_allowed_group_object_id
  sign_in_audience        = local.entra_sign_in_audience
}

module "control_plane" {
  source = "./modules/control-plane"

  name                         = var.control_plane_name
  resource_group_name          = data.azurerm_resource_group.this.name
  location                     = local.location
  container_app_environment_id = azurerm_container_app_environment.this.id
  image                        = local.control_plane_image
  key_vault_id                 = azurerm_key_vault.this.id
  key_vault_uri                = azurerm_key_vault.this.vault_uri

  catalog_connection_secret_name = local.catalog_connection_secret_name
  jwt_signing_key_secret_name    = local.jwt_signing_key_secret_name

  # Compute belongs to the worker app; the API replica does API + scheduler + sync work only.
  worker_enabled = false
  # The GUI is a separate origin on Container Apps (one ingress FQDN per app).
  cors_allowed_origins = [local.gui_url]

  bootstrap_admin_username             = var.admin_username
  bootstrap_admin_password_secret_name = local.admin_password_secret_name

  azure_ad_allowed_tenant_ids = local.effective_tenant_ids
  azure_ad_client_id          = local.effective_entra_client
  azure_ad_default_role       = var.azure_ad_default_role

  # The GUI chat assistant, on the estate's own Foundry account and MCP server (see chat_assistant_on).
  assistant_enabled                               = local.chat_assistant_on
  assistant_foundry_project_endpoint              = local.chat_assistant_on ? local.ai_foundry_endpoint : ""
  assistant_foundry_model_deployment_name         = local.chat_assistant_on ? var.ai_foundry_model_name : ""
  assistant_foundry_transcription_deployment_name = local.chat_assistant_on ? var.ai_foundry_transcription_model_name : ""
  assistant_mcp_server_url                        = local.chat_assistant_on ? module.mcp[0].mcp_url : ""
  ai_context_enabled                              = var.ai_context_enabled

  acr_id           = local.acr_id
  acr_login_server = local.acr_login_server
  min_replicas     = var.control_plane_min_replicas
  max_replicas     = var.control_plane_max_replicas
  private_networks = var.private_networks
  # The estate's configuration: partitions, the values every run resolves with, repositories, the node credential.
  settings = local.control_plane_settings
  tags     = var.tags

  # The app reads the generated secrets and the connections at creation, and migrates the catalog and the OSDU module at
  # startup, retrying until the grants hold.
  depends_on = [
    azurerm_resource_deployment_script_azure_power_shell.secrets,
    azurerm_key_vault_secret.catalog_db,
    azurerm_key_vault_secret.connection,
    azurerm_mssql_database.catalog,
    azurerm_mssql_database.connection,
    azurerm_mssql_firewall_rule.allow_azure_services,
  ]
}

module "worker" {
  source = "./modules/worker"

  name                         = var.worker_name
  resource_group_name          = data.azurerm_resource_group.this.name
  location                     = local.location
  container_app_environment_id = azurerm_container_app_environment.this.id
  image                        = local.worker_image
  key_vault_id                 = azurerm_key_vault.this.id
  key_vault_uri                = azurerm_key_vault.this.vault_uri
  pool                         = var.worker_pool
  control_plane_url            = local.control_plane_url
  node_token_secret_name       = local.node_token_secret_name
  private_networks             = var.private_networks
  secret_env                   = local.worker_secret_env
  acr_id                       = local.acr_id
  acr_login_server             = local.acr_login_server
  max_replicas                 = var.worker_max_replicas
  tags                         = var.tags

  depends_on = [
    azurerm_resource_deployment_script_azure_power_shell.secrets,
    azurerm_key_vault_secret.catalog_db,
    azurerm_mssql_database.catalog,
    azurerm_mssql_firewall_rule.allow_azure_services,
  ]
}

module "gui" {
  source = "./modules/gui"

  name                         = var.gui_name
  resource_group_name          = data.azurerm_resource_group.this.name
  location                     = local.location
  container_app_environment_id = azurerm_container_app_environment.this.id
  image                        = local.gui_image
  api_base_url                 = local.control_plane_url
  acr_id                       = local.acr_id
  acr_login_server             = local.acr_login_server
  tags                         = var.tags
}

# The MCP server over streamable HTTP: the chat assistant's tool source, and any other remote MCP client's that
# presents a SQLFlow bearer token. It answers only on its own host name.
module "mcp" {
  source = "./modules/mcp"
  count  = local.mcp_enabled ? 1 : 0

  name                         = var.mcp_name
  resource_group_name          = data.azurerm_resource_group.this.name
  location                     = local.location
  container_app_environment_id = azurerm_container_app_environment.this.id
  image                        = local.mcp_image
  control_plane_url            = local.control_plane_url
  gui_url                      = local.gui_url
  allowed_hosts                = local.mcp_fqdn
  acr_id                       = local.acr_id
  acr_login_server             = local.acr_login_server
  tags                         = var.tags
}

# Optional: an AI Foundry account + project beside the estate. The app identities are granted caller access
# (Cognitive Services User), so anything they run can call models keylessly; the control plane's identity is granted
# the OpenAI-inference role (Responses API + audio transcription) when the chat assistant is on.
module "ai_foundry" {
  source = "./modules/ai-foundry"
  count  = local.ai_foundry_enabled ? 1 : 0

  name                        = var.ai_foundry_name
  resource_group_name         = data.azurerm_resource_group.this.name
  location                    = local.location
  project_name                = var.ai_foundry_project_name
  model_name                  = var.ai_foundry_model_name
  model_version               = var.ai_foundry_model_version
  model_capacity              = var.ai_foundry_model_capacity
  transcription_model_name    = var.ai_foundry_transcription_model_name
  transcription_model_version = var.ai_foundry_transcription_model_version
  user_principal_ids          = [module.control_plane.identity_principal_id, module.worker.identity_principal_id]
  agent_principal_ids         = local.chat_assistant_on ? [module.control_plane.identity_principal_id] : []
  tags                        = var.tags
}
