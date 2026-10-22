# The dev environment of OSDU Delivery: the estate deploy-dev.ps1 rolls images out to, and the variables deploy-dev.ps1
# -Infrastructure plans its Terraform with (the GitHub Deploy workflow runs the same script). Replace every
# <placeholder> once, or leave this file as it is and keep the environment's own copy outside the repository
# (OSDU_ENVIRONMENT_DIR; osdu/deploy/README.md, Environments): the scripts refuse to run on a placeholder. No secret
# goes in this file, and none reaches Terraform: the deployment generates the JWT signing key, the initial admin's
# password and the node credential into the estate's vault, and the secrets only a person has (each partition's OSDU
# client secret, a repository's token or the GitHub App's private key) are put in the vault by hand under the names below. The OSDU partitions come from
# outside this file too (below).

subscription_id     = "<dev subscription id>"
resource_group_name = "<dev resource group, created once>"
# The registry the images are built in, in that resource group (created once, like the group).
acr_name = "<dev container registry name>"
# The region: null takes the resource group's.
# location = "swedencentral"
tags = {
  environment = "dev"
  product     = "osdu-delivery"
}

# Sign-in: the registration Terraform creates for this environment, and the group whose members may sign in.
entra_app_display_name           = "OSDU Delivery (dev)"
entra_app_unique_name            = "osdu-delivery-dev"
azure_ad_allowed_group_object_id = "<object id of the group allowed to sign in to dev>"
# Where the deploying identity may not create app registrations (an organization's tenant often does not allow it), use
# an existing registration instead, with its redirect URI set to the GUI's address:
# provision_entra_app         = false
# azure_ad_client_id          = "<client id of the existing registration>"
# azure_ad_allowed_tenant_ids = ["<tenant id>"]

# SQL: a server created here, administered by the estate's setup identity, the apps signing in as their managed
# identities; the deployment creates their database users itself, and makes the group below owner of every database.
# For an existing server or Managed Instance whose databases exist, set existing_sql_server; an Entra administrator of
# that server then runs the sql_setup_script output. The flows name the databases in three-part names, so they keep
# these names.
sql_admins = [
  { name = "<dev SQL administrators group name>", object_id = "<dev SQL administrators group object id>", type = "group" },
]
# existing_sql_server = "<server>.database.windows.net"
catalog_database_name = "OsduDelivery"
# The data the flows work in, given to every run as OSDU_DATA_DB.
sql_connections = {
  OSDU_DATA_DB = { database = "OsduData" }
}

# The storage account the flows read their source files from, given to every run as LANDING_URL.
landing_storage = {}

# The flow repository the control plane syncs. A private one is read either with the estate's GitHub App (github_app:
# read-only, installed on the repository, its private key put in the estate's vault by hand; it serves every GitHub
# repository below that names no token, sqlflow/docs/reference/guides/github-app.md) or with its own token (for GitHub,
# a fine-grained token with read access to the repository's contents) put in the vault by hand under the secret name
# given here.
# github_app = {
#   app_id                  = "<the App's client ID>"
#   private_key_secret_name = "github-app-private-key"
# }
repositories = {
  "<flow repository name>" = {
    remote_url             = "<https URL of the flow repository>"
    credential_secret_name = "<vault secret holding the repository's read token>"
  }
}

# The MCP server, and the chat assistant on an AI Foundry model beside it.
mcp_enabled           = true
ai_foundry_name       = "<globally unique AI Foundry account name for dev>"
ai_foundry_model_name = "gpt-5.1"
ai_context_enabled    = false

# The OSDU partitions this environment delivers to never come from this file: their endpoints, client ids and access
# groups belong to the platform they deliver to. They are TF_VAR_osdu_partitions, a line of the git-ignored
# .sqlflow/deploy/dev.env on a workstation and the GitHub environment's secret TF_VAR_OSDU_PARTITIONS in the Deploy
# workflow, holding the variable's value as JSON:
#   {"<data-partition-id>": {"default": true, "description": "...", "url": "https://...", "token_url": "https://...",
#     "scope": "...", "client_id": "...", "acl_owner": "...", "acl_viewer": "...", "legal_tag": "..."}}
# Each partition's client secret is put in the estate's vault by hand as osdu-<data-partition-id>-client-secret (or the
# client_secret_name the partition names); terraform output osdu_partitions lists them.
