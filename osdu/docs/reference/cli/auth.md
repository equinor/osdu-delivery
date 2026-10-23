---
id: delivery-cli-auth
title: "sqlflow auth in OSDU Delivery: which delivery paths use the Azure credential it checks"
type: cli-command
summary: "Which OSDU Delivery paths use the Azure credential sqlflow auth verifies (Key Vault references, payload and work storage), and what it does not check."
keywords:
  - azure authentication
  - sqlflow_azure_auth
  - managed identity
  - key vault reference
  - "${keyvault:...}"
  - payload storage
  - work batches
  - abfss
  - osdu token
  - node identity
related:
  - cli-auth
  - delivery-cli-worker
  - delivery-cli-config
  - delivery-concept-environment-variables
  - delivery-flow-delivery
  - concept-connections-and-secrets
sourceRefs:
  - sqlflow/src/SqlFlow.Cli/Program.cs
  - sqlflow/src/SqlFlow.Azure/AzureKeyVaultSecretProvider.cs
  - sqlflow/src/SqlFlow.Core/Storage/AzureBlobLocation.cs
  - osdu/src/SqlFlow.Delivery/Storage/FileStore.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryServices.cs
  - osdu/src/SqlFlow.Delivery/Engine/Retrieval/RetrievalRunner.cs
  - sqlflow/src/SqlFlow.Core/Secrets/SuppliedReferences.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Hosting/OsduModuleDatabase.cs
  - osdu/src/SqlFlow.Delivery.Telemetry/TelemetryOptions.cs
  - osdu/deploy/bicep/worker.bicep
  - osdu/deploy/bicep/control-plane.bicep
---

# sqlflow auth in OSDU Delivery

OSDU Delivery's command line is `sqlflow`: SQLFlow's CLI with the OSDU verbs added. `auth` is SQLFlow's verb: it reports
`SQLFLOW_AZURE_AUTH` and the mode it resolves to, then acquires a real token for a scope through the one credential
factory every Azure path shares. The modes, the `AZURE_*` variables, the default chain and its in-Azure detection, the
output and the exit codes are documented in [sqlflow auth](../../../../sqlflow/docs/reference/cli/auth.md). This page
says which OSDU Delivery paths that credential is, so a successful `sqlflow auth` means something concrete here.

## Synopsis

```bash
sqlflow auth [--scope storage|keyvault|arm|<uri>]
```

## What the credential is used for

| Path | Where it runs | Scope to check |
| --- | --- | --- |
| Resolving a `${keyvault:vault/secret}` reference anywhere OSDU Delivery reads one: a flow's `target.auth` secrets, its source connection, a central configuration value that is itself a reference, and the telemetry header and connection references. | The process that resolves it: a node for a run, the control plane for what it runs while a person waits. | `keyvault` |
| Reading the payload files a record points at (`source.payloads`, an interface's `files` and `bulk`). | The process running the flow: a node, or the CLI for a local `sqlflow run`. | `storage` |
| Writing and reading a run's work batches (`source.work`). | The process running the flow. | `storage` |
| Writing a retrieval flow's output (`target.location`). | The process running the flow. | `storage` |

The storage paths use the credential for a location on Azure Storage: an `abfss://`, `abfs://`, `wasbs://` or `wasb://`
URI, or an `https://` URL whose host is a blob or dfs endpoint (`<account>.blob.core.windows.net`,
`<account>.dfs.core.windows.net`). A local path needs no credential.

A green `sqlflow auth` run as the node's identity therefore confirms the credential all of these read, before a run needs
it. That matters most on a node, where every one of them resolves: a credential problem there surfaces as a failed run
rather than a start-up error.

```bash
# on a node, as the node's managed identity: can it read the vault the OSDU secrets are in?
SQLFLOW_AZURE_AUTH=mi AZURE_CLIENT_ID=<the node identity client id> sqlflow auth --scope keyvault

# and the storage account the payload files and work batches are on?
SQLFLOW_AZURE_AUTH=mi AZURE_CLIENT_ID=<the node identity client id> sqlflow auth --scope storage
```

The shipped Azure templates (`osdu/deploy/bicep`) set `SQLFLOW_AZURE_AUTH=mi` and `AZURE_CLIENT_ID` on the control
plane and worker apps, so the identity `sqlflow auth` checks inside those containers is the identity the flows use.

## What it does not check

- **OSDU's own authentication.** A flow authenticates to OSDU with its `target.auth` block (`oauth2ClientCredentials`,
  `bearer`, `apiKeyHeader`, `basic` or `none`), whose secrets are references the node resolves; none of these uses the
  Azure credential to obtain the OSDU token, and OSDU's entitlements decide what that principal may write. To check that
  end, probe the flow's target from its page in the GUI, which the control plane runs under the flow's own credentials
  ([Delivery flow](../flow/delivery.md)).
- **Database logins.** The ingestion database and the catalog database, which holds the module's `osdu` schema, are
  reached with their connection strings (`${env:OSDU_DATA_DB}`, `SQLFLOW_CATALOG_DB`); `sqlflow auth` does not test
  them. `sqlflow db status` tests the module database ([sqlflow db](db.md)).
- **The control plane's credential.** That is `sqlflow login` and `sqlflow whoami`
  ([Control-plane verbs](control-plane.md)).

## See also

- [sqlflow auth](../../../../sqlflow/docs/reference/cli/auth.md): modes, variables, output and exit codes.
- [sqlflow worker](worker.md): what a node resolves and when.
- [sqlflow config](config.md): central configuration values, which may themselves be `${keyvault:...}` references.
- [Environment variables](../concepts/environment-variables.md): `SQLFLOW_AZURE_AUTH` and the `AZURE_*` family.
- [Connections and secrets](../../../../sqlflow/docs/reference/concepts/connections-and-secrets.md): how references resolve.
