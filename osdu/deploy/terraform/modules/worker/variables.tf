variable "name" {
  description = "Name of the Container App (and the prefix for its managed identity). One app per pool, e.g. osdu-delivery-worker-onprem."
  type        = string
  default     = "osdu-delivery-worker"
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
  description = "Container image reference, e.g. myregistry.azurecr.io/osdu-delivery-worker:latest"
  type        = string
}

variable "key_vault_id" {
  description = "Resource id of the Key Vault holding the node token and flow secrets; the app identity is granted Key Vault Secrets User on it."
  type        = string
}

variable "key_vault_uri" {
  description = "The vault's URI with its trailing slash (https://<vault>.vault.azure.net/)."
  type        = string
}

variable "control_plane_url" {
  description = "The control plane base URL the node polls (https://<control-plane-fqdn>); the scale rule reads the pool's replica target from the same host."
  type        = string
}

variable "node_token_secret_name" {
  description = "Key Vault secret name holding the node credential the node presents to the control plane, and the scale rule to the scale-target endpoint: the token the deployment generates into the vault and the control plane accepts (ControlPlane:Bootstrap:NodeTokenReference)."
  type        = string
}

variable "pool" {
  description = "The pool this app serves (SQLFLOW_WORKER_POOL and the scale-target query). Empty takes untargeted runs only."
  type        = string
  default     = ""
}

variable "private_networks" {
  description = "The private ranges (CIDR, comma separated) the nodes may reach (SQLFLOW_DELIVERY_PRIVATE_NETWORKS). Empty reaches public addresses only."
  type        = string
  default     = ""
}

variable "secret_env" {
  description = "Environment variables a node itself reads from the vault, beside its node token: SQLFLOW_CATALOG_DB, the catalog's connection, which the node reads and writes the ledger in the `osdu` schema through. Everything a run reads besides is handed to the run by the control plane."
  type = list(object({
    name        = string
    secret_name = string
  }))
  default = []
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

variable "max_replicas" {
  description = "Upper bound for scale out."
  type        = number
  default     = 10

  validation {
    condition     = var.max_replicas >= 1
    error_message = "max_replicas must be at least 1."
  }
}

variable "cpu" {
  description = "vCPU per replica; must form a valid Container Apps consumption pair with memory."
  type        = number
  default     = 1
}

variable "memory" {
  description = "Memory per replica, paired with cpu."
  type        = string
  default     = "2Gi"
}

variable "tags" {
  description = "Tags set on the app and its identity."
  type        = map(string)
  default     = {}
}
