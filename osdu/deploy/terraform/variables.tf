# The inputs of the estate, one for each parameter of osdu/deploy/bicep/main.bicep (snake_case here), plus the
# subscription and resource group Terraform needs to be told.

# ------------------------------------------------------------------------------------------------- where it goes ---

variable "subscription_id" {
  description = "Subscription to deploy into. Null reads ARM_SUBSCRIPTION_ID."
  type        = string
  default     = null
}

variable "resource_group_name" {
  description = "An existing resource group the whole estate is created in."
  type        = string
}

variable "location" {
  description = "Azure region. Null takes the resource group's."
  type        = string
  default     = null
}

variable "tags" {
  description = "Tags set on every resource this configuration creates."
  type        = map(string)
  default     = {}
}

# -------------------------------------------------------------------------------------------------------- images ---

variable "image_tag" {
  description = "The tag of this repository's images in the estate's registry: the commit they were built from, which deploy.ps1 passes (-Infrastructure). Each app's image is <registry>/osdu-delivery-<app>:<image_tag> unless its own image variable names one. An image is set when its app is created; after that deploy.ps1 rolls the apps forward and Terraform leaves the image they serve as it is."
  type        = string
  default     = ""
}

variable "control_plane_image" {
  description = "Container image reference for the control plane, e.g. myregistry.azurecr.io/osdu-delivery-control-plane:<tag>. Empty: <registry>/osdu-delivery-control-plane:<image_tag>."
  type        = string
  default     = ""
}

variable "worker_image" {
  description = "Container image reference for the worker, e.g. myregistry.azurecr.io/osdu-delivery-worker:<tag>. Empty: <registry>/osdu-delivery-worker:<image_tag>."
  type        = string
  default     = ""
}

variable "gui_image" {
  description = "Container image reference for the GUI, e.g. myregistry.azurecr.io/osdu-delivery-gui:<tag>. Empty: <registry>/osdu-delivery-gui:<image_tag>."
  type        = string
  default     = ""
}

variable "mcp_enabled" {
  description = "Deploy the MCP server (built from osdu/deploy/docker/mcp.Dockerfile), and with an AI Foundry model, the chat assistant on it. Its image is <registry>/osdu-delivery-mcp:<image_tag> unless mcp_image names one."
  type        = bool
  default     = false
}

variable "mcp_image" {
  description = "Container image for the MCP server in HTTP mode, e.g. myregistry.azurecr.io/osdu-delivery-mcp:<tag>. Naming one deploys the MCP server whatever mcp_enabled says; empty with mcp_enabled off skips it, and with it the chat assistant."
  type        = string
  default     = ""
}

variable "acr_name" {
  description = "Name of a container registry in THIS resource group holding the images: each app identity is granted AcrPull on it. Empty for a public registry, or one you authorize yourself via acr_login_server."
  type        = string
  default     = ""
}

variable "acr_login_server" {
  description = "Login server of a registry outside this resource group (grant AcrPull to the app identities yourself). Ignored when acr_name is set."
  type        = string
  default     = ""
}

# ------------------------------------------------------------------------------------------------------- network ---

variable "existing_sql_server" {
  description = "Address of an existing SQL Server or Azure SQL Managed Instance whose databases the estate uses, instead of creating a server here: host or host,port (a Managed Instance's private FQDN <mi>.<dns-zone>.database.windows.net, its public endpoint <mi>.public.<dns-zone>.database.windows.net,3342, or any reachable SQL Server). Set, nothing SQL is created: no server, firewall rule or database. The databases named by catalog_database_name and each of sql_connections must already exist there, the network path is yours to provide (for a VNet-only Managed Instance, set infrastructure_subnet_id so the apps reach its VNet), and an Entra administrator of the server runs the sql_setup_script output to grant the apps their access. Only their connection secrets are written here. Empty creates an Azure SQL logical server and the databases."
  type        = string
  default     = ""
}

variable "infrastructure_subnet_id" {
  description = "Resource id of a VNet subnet to integrate the Container Apps environment into: a workload-profiles environment needs a subnet of at least /27 delegated to Microsoft.App/environments. Empty deploys the environment without VNet integration."
  type        = string
  default     = ""
}

variable "private_networks" {
  description = "The private ranges (CIDR, comma separated) the apps may reach: with infrastructure_subnet_id set, the VNet ranges an OSDU, a storage account or a proxy behind a private endpoint resolves to. Empty reaches public addresses only."
  type        = string
  default     = ""
}

# ------------------------------------------------------------------------------------------------------- secrets ---

variable "sql_authentication" {
  description = "How the apps sign in to SQL. entra (the default): each app as its own managed identity (Authentication=Active Directory Default), so no connection carries a credential; a server created here gets Entra-only authentication, administered by the estate's setup identity, which creates the apps' database users inside every deployment (osdu/deploy/setup/Setup-Estate.ps1). With existing_sql_server, an Entra administrator of that server runs the sql_setup_script output instead. password: the SQL login below, its password in each connection string in the vault."
  type        = string
  default     = "entra"

  validation {
    condition     = contains(["entra", "password"], var.sql_authentication)
    error_message = "sql_authentication must be entra or password."
  }
}

variable "sql_admins" {
  description = "Entra authentication, server created here: the people (or groups) given db_owner in every database of the estate, inside every deployment: name as Entra shows it (a user's sign-in name, a group's display name), object_id, and type user or group. The server's own administrator is the estate's setup identity, so a deployment needs no person's rights to grant the apps their access."
  type = list(object({
    name      = string
    object_id = string
    type      = optional(string, "user")
  }))
  default = []

  validation {
    condition     = alltrue([for a in var.sql_admins : contains(["user", "group"], a.type) && can(regex("^[0-9a-fA-F-]{36}$", a.object_id))])
    error_message = "Each of sql_admins has type user or group and an object_id that is a GUID."
  }
}

variable "sql_admin_login" {
  description = "Password authentication only: the SQL login the apps sign in with, the admin of the server created here, or a login on the existing server or Managed Instance."
  type        = string
  default     = "osdudeliveryadmin"
}

variable "sql_admin_password" {
  description = "Password authentication only: the password of sql_admin_login (Azure SQL enforces complexity). Single quotes and semicolons are handled; the value is quoted into the connection strings."
  type        = string
  sensitive   = true
  default     = null
}

variable "admin_username" {
  description = "Username of the initial admin user bootstrap provisioning creates on first start. Its password is generated into the vault by the deployment (osdu-delivery-admin-password) the first time, and read from there."
  type        = string
  default     = "admin"
}

# ------------------------------------------------------------------------------------------------------- sign-in ---

variable "provision_entra_app" {
  description = "Create and own the Entra app registration users sign in with (an app role and assignment required, so only assigned members can sign in). On enables \"Sign in with Microsoft\" and ignores azure_ad_allowed_tenant_ids/azure_ad_client_id; the deploying identity then needs directory write (Application Administrator or Application.ReadWrite.All), and assigning a group needs Entra ID P1. Off for local-only sign-in, or to point at an externally managed registration."
  type        = bool
  default     = true
}

variable "azure_ad_allowed_group_object_id" {
  description = "Object id of the Entra security group whose members may sign in when provision_entra_app is on. Empty still enforces assignment required, so no one signs in until a group or users are assigned in the enterprise application."
  type        = string
  default     = ""
}

variable "entra_app_display_name" {
  description = "Display name of the app registration created when provision_entra_app is on."
  type        = string
  default     = "OSDU Delivery"
}

variable "entra_app_unique_name" {
  description = "Stable name the registration's app role id is derived from, so a role stays the same across applies. Lowercase, no spaces."
  type        = string
  default     = "osdu-delivery"
}

variable "azure_ad_additional_allowed_tenant_ids" {
  description = "Provisioned app mode only: further Entra tenant ids, beyond this deployment's own (always trusted), whose users may also sign in. A non-empty list makes the registration multi-tenant; each named tenant's admin must still consent and assign the SqlFlow.User role to their own users."
  type        = list(string)
  default     = []
}

variable "azure_ad_allowed_tenant_ids" {
  description = "External app mode only (provision_entra_app off): tenant ids allowed for single sign-on against a registration you manage yourself. Set with azure_ad_client_id; empty for local sign-in only."
  type        = list(string)
  default     = []
}

variable "azure_ad_client_id" {
  description = "External app mode only (provision_entra_app off): client id of the SPA app registration users sign in with."
  type        = string
  default     = ""
}

variable "azure_ad_default_role" {
  description = "Role a first-time SSO user is provisioned with (least privilege by default; an admin raises it afterwards in the GUI)."
  type        = string
  default     = "viewer"
}

# ------------------------------------------------------------------------------------------- flows and the fleet ---

variable "osdu_partitions" {
  description = "The OSDU partitions this deployment delivers to, by data-partition-id: at least one, since OSDU Delivery works against a partition and the control plane is not ready without one, and with several, exactly one marked default (the one a run that names none runs in). Each gives the values its runs resolve their flows' $${env:...} references with: the platform (url as OSDU_URL, token_url as OSDU_TOKEN_URL, scope as OSDU_SCOPE), client_id as OSDU_CLIENT_ID, optionally acl_owner, acl_viewer and legal_tag (OSDU_ACL_OWNER, OSDU_ACL_VIEWER, OSDU_LEGAL_TAG), and any further name a flow spells under properties. The client secret is never given here: it is a secret put in this estate's vault by hand, named by client_secret_name (osdu-<partition>-client-secret when left out), and the partition reads it as OSDU_CLIENT_SECRET = $${keyvault:<vault>/<secret>}. The control plane registers them when it starts (Osdu:Partitions), reports a client secret it cannot read yet as a failed start-up step, and only this configuration changes them."
  type = map(object({
    default            = optional(bool, false)
    description        = optional(string)
    url                = string
    token_url          = string
    scope              = string
    client_id          = string
    client_secret_name = optional(string)
    acl_owner          = optional(string)
    acl_viewer         = optional(string)
    legal_tag          = optional(string)
    properties         = optional(map(string), {})
  }))

  validation {
    condition     = length(var.osdu_partitions) > 0
    error_message = "Name at least one OSDU partition: OSDU Delivery works against a partition, and the control plane is not ready without one."
  }

  validation {
    condition     = alltrue([for id in keys(var.osdu_partitions) : can(regex("^[A-Za-z0-9_.-]{1,200}$", id))])
    error_message = "A partition is named by its data-partition-id: letters, digits, underscore, hyphen and dot, at most 200 characters."
  }

  validation {
    condition     = length(var.osdu_partitions) <= 1 || length([for p in values(var.osdu_partitions) : p if p.default]) == 1
    error_message = "With several partitions, mark exactly one default = true: the partition a run that names none runs in."
  }

  validation {
    condition     = alltrue(flatten([for p in values(var.osdu_partitions) : [for name in keys(p.properties) : can(regex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", name))]]))
    error_message = "A property is named as a flow spells it in $${env:NAME}: a letter or underscore, then letters, digits and underscores, at most 64 characters."
  }

  validation {
    condition = alltrue(flatten([for p in values(var.osdu_partitions) : [
      for name, value in p.properties : !can(regex("(?i)(SECRET|PASSWORD|TOKEN)$|API_?KEY", name)) || can(regex("^\\$\\{(env|keyvault):[^}]+\\}$", trimspace(value)))
    ]]))
    error_message = "A property whose name says it is a secret (ending in SECRET, PASSWORD or TOKEN, or naming an API key) takes a $${keyvault:<vault>/<secret>} reference, never the secret itself."
  }
}

variable "properties" {
  description = "Further values set for no partition (Osdu:Properties:<NAME>), by the name a flow spells in $${env:NAME}: what every run of every kind resolves with, beside the SQL connections and the landing storage this configuration sets itself. A value is a setting or a $${keyvault:<vault>/<secret>} reference to a secret put in the vault by hand, never the secret itself."
  type        = map(string)
  default     = {}

  validation {
    condition     = alltrue([for name in keys(var.properties) : can(regex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", name))])
    error_message = "A property is named as a flow spells it in $${env:NAME}: a letter or underscore, then letters, digits and underscores, at most 64 characters."
  }

  validation {
    condition = alltrue([
      for name, value in var.properties : !can(regex("(?i)(SECRET|PASSWORD|TOKEN)$|API_?KEY", name)) || can(regex("^\\$\\{(env|keyvault):[^}]+\\}$", trimspace(value)))
    ])
    error_message = "A property whose name says it is a secret (ending in SECRET, PASSWORD or TOKEN, or naming an API key) takes a $${keyvault:<vault>/<secret>} reference, never the secret itself."
  }
}

variable "sql_connections" {
  description = "The SQL databases the estate's flows read and write, by the name a flow spells in $${env:NAME} (OSDU_DATA_DB, OsduData by default, where the pre-ingestion flows land the files, the ingestion flows load the keyed tables and the delivery flows read them). Each is either a database on the estate's server (database: created here unless create is false; access write lets the worker create, load and read its tables, read lets it read them; the control plane always reads), its connection string built here with no credential (each app signs in as its own identity), its users granted inside the deployment; or a database elsewhere (secret_name: a vault secret put there by hand holding the whole connection string, its access yours to grant). Every run is given each as $${keyvault:<vault>/<secret>}, which a node resolves with its own identity."
  type = map(object({
    database    = optional(string)
    create      = optional(bool, true)
    access      = optional(string, "write")
    secret_name = optional(string)
  }))
  # The data database the shipped flows read as OSDU_DATA_DB, which every estate has had: an estate applied without
  # naming its connections (or naming them null) keeps it.
  nullable = false
  default = {
    OSDU_DATA_DB = { database = "OsduData" }
  }

  validation {
    condition     = alltrue([for name in keys(var.sql_connections) : can(regex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", name))])
    error_message = "A connection is named as a flow spells it in $${env:NAME}: a letter or underscore, then letters, digits and underscores, at most 64 characters."
  }

  validation {
    condition     = alltrue([for name in keys(var.sql_connections) : upper(name) != "SQLFLOW_CATALOG_DB"])
    error_message = "SQLFLOW_CATALOG_DB is the estate's own database, set by this configuration; name the connection otherwise."
  }

  validation {
    condition     = alltrue([for c in values(var.sql_connections) : (c.database != null) != (c.secret_name != null)])
    error_message = "Each SQL connection names exactly one of database (on the estate's server) and secret_name (a vault secret holding the connection string of a database elsewhere)."
  }

  validation {
    condition     = alltrue([for c in values(var.sql_connections) : contains(["read", "write"], c.access)])
    error_message = "A SQL connection's access is read or write."
  }
}

variable "sql_connection_sku" {
  description = "SKU of each database sql_connections creates (ignored when existing_sql_server is set). They carry the record data, so they are sized apart from the metadata."
  type        = string
  default     = "S1"
}

variable "repositories" {
  description = "The flow repositories the control plane syncs, by name (ControlPlane:Bootstrap:Repositories): remote_url, branch, how often to sync, and for a private remote either the estate's GitHub App (github_app, used for every repository on its GitHub unless use_github_app is false) or the vault secret holding the repository's own token (credential_secret_name, put there by hand: a GitHub fine-grained token with read access to the repository's contents, which wins over the App) with its username where the host needs one. The control plane registers them at start and keeps them as configured; nobody registers them by hand."
  type = map(object({
    remote_url             = string
    branch                 = optional(string, "main")
    sync_interval_seconds  = optional(number, 300)
    credential_secret_name = optional(string)
    credential_username    = optional(string)
    use_github_app         = optional(bool, true)
    excluded_flow_paths    = optional(list(string), [])
  }))
  default = {}

  validation {
    condition     = alltrue([for name in keys(var.repositories) : can(regex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$", name))])
    error_message = "A repository is named with letters, digits, underscore, hyphen and dot, starting with a letter or digit, at most 100 characters."
  }

  validation {
    condition     = alltrue([for r in values(var.repositories) : can(regex("^https://", r.remote_url)) && r.sync_interval_seconds >= 30])
    error_message = "A repository's remote_url is an https:// URL, and it syncs at most every 30 seconds."
  }
}

variable "github_app" {
  description = "The GitHub App the estate's GitHub repositories are read with, in place of a token per repository (sqlflow/docs/reference/guides/github-app.md): its client ID or App ID (app_id), the vault secret holding its private key (private_key_secret_name: the .pem GitHub generated, put there by hand), and for GitHub Enterprise Server or GHE.com its API root (api_url). A repository on the App's GitHub that names no credential_secret_name and leaves use_github_app on is read with a short-lived token the App mints for that repository alone, by the control plane and by each worker with its own identity; no token is stored anywhere. Null reads each repository with its own token, or with none."
  type = object({
    app_id                  = string
    private_key_secret_name = string
    api_url                 = optional(string)
  })
  default = null

  validation {
    condition     = var.github_app == null ? true : can(regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", var.github_app.app_id))
    error_message = "github_app.app_id is the App's client ID (like Iv23liAbCdEf) or its numeric App ID."
  }

  validation {
    condition     = var.github_app == null ? true : can(regex("^[0-9A-Za-z-]{1,127}$", var.github_app.private_key_secret_name))
    error_message = "github_app.private_key_secret_name is a Key Vault secret name: letters, digits and hyphens, at most 127 characters."
  }

  validation {
    condition     = var.github_app == null ? true : (var.github_app.api_url == null ? true : can(regex("^https://[^/?#@]+(/[^?#]*)?$", var.github_app.api_url)))
    error_message = "github_app.api_url is the https:// API root of GitHub Enterprise Server (https://<host>/api/v3) or GHE.com (https://api.<name>.ghe.com), with no user, query or fragment."
  }
}

variable "landing_storage" {
  description = "The storage account the estate's flows read their source files from: created here (account_name, derived from the resource group when left out) or adopted (existing_account_id), with a container. The worker's identity may read and write it and the control plane's read it, and every run is given its location as reference_name (LANDING_URL: abfss://<container>@<account>.dfs.core.windows.net), which a flow names in its source location ($${env:LANDING_URL}/<folder>). Null gives the estate none."
  type = object({
    account_name        = optional(string)
    existing_account_id = optional(string)
    container           = optional(string, "landing")
    reference_name      = optional(string, "LANDING_URL")
  })
  default = null

  validation {
    condition     = var.landing_storage == null ? true : can(regex("^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$", var.landing_storage.container))
    error_message = "landing_storage.container is a container name: 3 to 63 lowercase letters, digits and hyphens."
  }

  validation {
    condition     = var.landing_storage == null ? true : can(regex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", var.landing_storage.reference_name))
    error_message = "landing_storage.reference_name is named as a flow spells it in $${env:NAME}."
  }
}

variable "worker_pool" {
  description = "The pool the worker app serves. Empty takes untargeted runs only."
  type        = string
  default     = ""
}

variable "worker_max_replicas" {
  description = "Upper bound for worker scale out."
  type        = number
  default     = 10

  validation {
    condition     = var.worker_max_replicas >= 1
    error_message = "worker_max_replicas must be at least 1."
  }
}

variable "control_plane_min_replicas" {
  description = "Minimum control plane replicas. Keep at 1 so the API is warm."
  type        = number
  default     = 1

  validation {
    condition     = var.control_plane_min_replicas >= 1
    error_message = "control_plane_min_replicas must be at least 1."
  }
}

variable "control_plane_max_replicas" {
  description = "Maximum control plane replicas. Keep at 1: the run queue is owned by exactly one replica (the dispatch lease), so an extra replica adds no dispatch capacity and refuses every node call that lands on it."
  type        = number
  default     = 1

  validation {
    condition     = var.control_plane_max_replicas >= 1
    error_message = "control_plane_max_replicas must be at least 1."
  }
}

# ------------------------------------------------------------------------------------- AI Foundry and the assistant ---

variable "ai_foundry_name" {
  description = "Name for an Azure AI Foundry account (also its endpoint subdomain, globally unique) deployed alongside the estate, with the control plane and worker identities granted caller access. Empty skips AI Foundry, and with it the chat assistant."
  type        = string
  default     = ""
}

variable "ai_foundry_project_name" {
  description = "Name of the Foundry project created under the account."
  type        = string
  default     = "osdu-delivery"
}

variable "ai_foundry_model_name" {
  description = "Model deployed under the account for the chat assistant, e.g. gpt-5.1 (one that serves the Responses API, and that the region still accepts for new deployments). Empty deploys no model, and leaves the assistant off."
  type        = string
  default     = ""
}

variable "ai_foundry_model_version" {
  description = "Version of that model. Empty lets the service pick the current default version."
  type        = string
  default     = ""
}

variable "ai_foundry_model_capacity" {
  description = "Capacity for the model deployment, in thousands of tokens per minute."
  type        = number
  default     = 30

  validation {
    condition     = var.ai_foundry_model_capacity >= 1
    error_message = "ai_foundry_model_capacity must be at least 1."
  }
}

variable "ai_foundry_transcription_model_name" {
  description = "Optional audio-transcription model deployed beside the chat model (e.g. gpt-4o-mini-transcribe), enabling the GUI chat assistant's server-side voice input. Empty deploys none."
  type        = string
  default     = ""
}

variable "ai_foundry_transcription_model_version" {
  description = "Version of the transcription model. Empty lets the service pick the current default version."
  type        = string
  default     = ""
}

variable "ai_context_enabled" {
  description = "Turn AI context on: questions starting with ! run read-only queries over the approved AI contexts, on the worker pool, with the connections those contexts open given to every run (sql_connections, properties)."
  type        = bool
  default     = false
}

# --------------------------------------------------------------------------------------------------------- names ---

variable "control_plane_name" {
  description = "Name of the Container App running the control plane."
  type        = string
  default     = "osdu-delivery-control-plane"
}

variable "worker_name" {
  description = "Name of the Container App running the worker pool."
  type        = string
  default     = "osdu-delivery-worker"
}

variable "gui_name" {
  description = "Name of the Container App running the GUI."
  type        = string
  default     = "osdu-delivery-gui"
}

variable "mcp_name" {
  description = "Name of the Container App running the MCP server."
  type        = string
  default     = "osdu-delivery-mcp"
}

variable "environment_name" {
  description = "Name of the Container Apps managed environment."
  type        = string
  default     = "osdu-delivery-env"
}

variable "log_analytics_name" {
  description = "Name of the Log Analytics workspace receiving container logs."
  type        = string
  default     = "osdu-delivery-logs"
}

variable "key_vault_name" {
  description = "Key Vault name (globally unique, 3-24 alphanumerics and hyphens). Null derives one from the resource group."
  type        = string
  default     = null

  validation {
    condition     = var.key_vault_name == null || (length(coalesce(var.key_vault_name, "kv-")) >= 3 && length(coalesce(var.key_vault_name, "kv-")) <= 24)
    error_message = "key_vault_name must be 3 to 24 characters."
  }
}

variable "sql_server_name" {
  description = "Azure SQL logical server name (globally unique, lowercase). Null derives one from the resource group."
  type        = string
  default     = null
}

variable "catalog_database_name" {
  description = "The metadata database: SQLFlow's catalog (pipelines, runs, schedules, lineage, sources, users) and, beside it, the module's osdu schema. Every run is given it as $${env:SQLFLOW_CATALOG_DB}, through which the flows read the osdu schema's views, and the flows name it in three-part names, so keep the name they use."
  type        = string
  default     = "OsduDelivery"
}

variable "setup_identity_name" {
  description = "Name of the estate's setup identity: the managed identity the deployment's own steps run as (osdu/deploy/setup/Setup-Estate.ps1), the Entra administrator of the SQL server created here and a Key Vault Secrets Officer of the estate's vault. No app runs as it."
  type        = string
  default     = "osdu-delivery-setup"
}

variable "osdu_database_name" {
  description = "No longer read, and ignored when set: the module's `osdu` schema (the record ledger, mappings, templates and the OSDU cache) lives in the catalog's database, catalog_database_name. Kept so an environment file that still sets it applies unchanged."
  type        = string
  default     = null
}

variable "sql_database_sku" {
  description = "Catalog database SKU (ignored when existing_sql_server is set). It carries the metadata, the run journal and the delivery ledger: modest, but polled continuously, so avoid serverless auto-pause."
  type        = string
  default     = "S1"
}

