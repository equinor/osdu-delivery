variable "name" {
  description = "Name of the Container App (and the prefix for its managed identity)."
  type        = string
  default     = "osdu-delivery-control-plane"
}

variable "resource_group_name" {
  description = "Resource group the app and its identity are created in."
  type        = string
}

variable "location" {
  description = "Azure region of the identity (the app follows its environment)."
  type        = string
}

variable "container_app_environment_id" {
  description = "Resource id of the Container Apps managed environment hosting the app."
  type        = string
}

variable "image" {
  description = "Container image reference, e.g. myregistry.azurecr.io/osdu-delivery-control-plane:latest"
  type        = string
}

variable "key_vault_id" {
  description = "Resource id of the Key Vault holding the secrets; the app identity is granted Key Vault Secrets User on it."
  type        = string
}

variable "key_vault_uri" {
  description = "The vault's URI with its trailing slash (https://<vault>.vault.azure.net/)."
  type        = string
}

variable "catalog_connection_secret_name" {
  description = "Key Vault secret name for the catalog ADO.NET connection string, which the delivery module's `osdu` schema is reached through too."
  type        = string
  default     = "osdu-delivery-catalog-db"
}

variable "jwt_signing_key_secret_name" {
  description = "Key Vault secret name for the JWT signing key (at least 32 bytes)."
  type        = string
  default     = "osdu-delivery-jwt-signing-key"
}

variable "jwt_issuer" {
  description = "JWT issuer the control plane validates."
  type        = string
  default     = "sqlflow-control-plane"
}

variable "jwt_audience" {
  description = "JWT audience the control plane validates."
  type        = string
  default     = "sqlflow"
}

variable "worker_enabled" {
  description = "Run the in-process worker inside the control plane (single-app mode). False when dedicated nodes (modules/worker) take the work, so the API replica does API work only."
  type        = bool
  default     = true
}

variable "cors_allowed_origins" {
  description = "Browser origins allowed to call the API, e.g. the GUI app URL. Empty when the GUI is same-origin behind a path-splitting front, or only non-browser clients call the API."
  type        = list(string)
  default     = []
}

variable "bootstrap_admin_password_secret_name" {
  description = "Key Vault secret name for the initial admin password. When set, bootstrap provisioning creates that admin on first start; empty skips user bootstrap."
  type        = string
  default     = ""
}

variable "bootstrap_admin_username" {
  description = "Username for the bootstrap admin. Only used when bootstrap_admin_password_secret_name is set."
  type        = string
  default     = "admin"
}

variable "private_networks" {
  description = "The private ranges (CIDR, comma separated) the reads of a flow may reach (SQLFLOW_DELIVERY_PRIVATE_NETWORKS). The same value the worker app takes. Empty reaches public addresses only."
  type        = string
  default     = ""
}

variable "proxy_known_networks" {
  description = "CIDRs of the ingress hops to trust for X-Forwarded-* headers. Empty keeps proxy trust off; per-client rate limiting then keys on the ingress hop address instead of the real client."
  type        = list(string)
  default     = []
}

variable "azure_ad_allowed_tenant_ids" {
  description = "Microsoft Entra tenant ids allowed to sign in to the GUI. Set with azure_ad_client_id to offer \"Sign in with Microsoft\"; empty for local sign-in only."
  type        = list(string)
  default     = []
}

variable "azure_ad_client_id" {
  description = "Client id of the SPA app registration users sign in with (its redirect URI must be the GUI origin)."
  type        = string
  default     = ""
}

variable "azure_ad_default_role" {
  description = "Role a first-time SSO user is provisioned with."
  type        = string
  default     = "viewer"
}

variable "assistant_enabled" {
  description = "Enable the GUI chat assistant (/api/v1/chat), with every agent run presenting a short-lived token delegated from the calling user to the MCP server. Needs the Foundry and MCP inputs below; this app's identity signs in to the Foundry account (no key), so grant it Cognitive Services OpenAI User there."
  type        = bool
  default     = false
}

variable "assistant_foundry_project_endpoint" {
  description = "The Foundry project endpoint the assistant runs against (https://<account>.services.ai.azure.com/api/projects/<project>). Required when assistant_enabled."
  type        = string
  default     = ""
}

variable "assistant_foundry_model_deployment_name" {
  description = "The model deployment the assistant runs on. Required when assistant_enabled."
  type        = string
  default     = ""
}

variable "assistant_foundry_transcription_deployment_name" {
  description = "Optional audio-transcription deployment in the same account; empty leaves voice input on the browser's built-in speech recognition."
  type        = string
  default     = ""
}

variable "assistant_mcp_server_url" {
  description = "The deployed MCP server endpoint (https://<mcp host>/mcp, the mcp_url output of modules/mcp), the assistant's tool source. Required when assistant_enabled."
  type        = string
  default     = ""

  validation {
    condition     = var.assistant_mcp_server_url == "" || startswith(var.assistant_mcp_server_url, "https://")
    error_message = "assistant_mcp_server_url must be an https:// URL."
  }
}

variable "ai_context_enabled" {
  description = "Turn AI context on (ControlPlane:AiContext:Enabled): questions starting with ! run read-only queries over the approved AI contexts, on the nodes."
  type        = bool
  default     = false
}

variable "acr_id" {
  description = "Resource id of a container registry the app identity is granted AcrPull on. Empty for a public registry, or one you authorize yourself."
  type        = string
  default     = ""
}

variable "acr_login_server" {
  description = "Login server of the registry the app pulls its image from with its identity (the acr_id registry's, or one you authorize yourself). Empty for a public registry."
  type        = string
  default     = ""
}

variable "min_replicas" {
  description = "Minimum replicas. Keep at 1 so the API is warm."
  type        = number
  default     = 1
}

variable "max_replicas" {
  description = "Maximum replicas. Keep at 1: the run queue is owned by exactly one replica (the dispatch lease)."
  type        = number
  default     = 1
}

variable "settings" {
  description = "Further plain settings, by environment variable name: configuration that is no secret, such as the OSDU partitions the control plane registers when it starts (Osdu__Partitions__<id>__...). A secret never goes here; a setting that needs one holds a $${keyvault:...} reference."
  type        = map(string)
  default     = {}
}

variable "tags" {
  description = "Tags set on the app and its identity."
  type        = map(string)
  default     = {}
}
