---
id: delivery-wiki-0020-explorer-partition-connection
title: "Decision 0020: The explorer reads a partition through the partition's own connection, never a flow's"
type: decision
summary: "Why the explorer signs in with the platform and client a partition's configuration names, rather than borrowing a delivery flow's connection."
keywords:
  - explorer connection
  - partition connection
  - borrowed connection
  - search without a flow
  - OSDU_URL
  - client credentials
referenceRefs:
  - delivery-concept-explorer
  - delivery-concept-partitions
  - delivery-concept-api
  - delivery-concept-environment-variables
  - delivery-guide-deployment
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Operations/PartitionConnection.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/ExploreOperation.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/TargetClients.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DirectOperationRunner.cs
  - osdu/deploy/terraform/main.tf
  - osdu/deploy/bicep/main.bicep
  - osdu/src/SqlFlow.Delivery.Setup/DevSetupWizard.cs
  - osdu/tests/SqlFlow.Delivery.Tests/PartitionConnectionTests.cs
updated: 2026-10-22
---
# 0020: The explorer reads a partition through the partition's own connection, never a flow's

Status: proposed. Documented in [the explorer](../../reference/concepts/explorer.md#how-it-reaches-osdu) and
[partitions](../../reference/concepts/partitions.md#configured-partitions).

## Context

The explorer shows what an OSDU partition holds, and nothing the delivery system keeps. Until this decision it had no way
in of its own: every read went through the connection of a delivery flow the control plane picked for the partition. Of
the active delivery flows, it took one that reached the partition (a flow naming its partitions had to serve it; a header
flow had to write the partition literally in its `data-partition-id` header, or keep a ledger there), by route (storage
first, then the other routes on the platform's own services) and then by flow name.

That made a view of OSDU depend on what the delivery system held:

- A deployment whose configuration named its partitions could not search them until a repository was synced and an
  active delivery flow reached each one, where what OSDU holds should be searchable with no flow in the system at all.
- The credentials a read used changed with the catalog. Deactivating or renaming a flow, or moving it to the dspdm or
  etp route, changed or removed the explorer's way in, and between two flows reaching the partition with different
  endpoints the name ordering decided.
- Every read parsed the catalog copy of every active delivery flow, and read the ledger directory for header flows,
  only to find a connection.

Meanwhile every deployment already names each partition's platform and client. The Terraform and Bicep templates write
`OSDU_URL`, `OSDU_TOKEN_URL`, `OSDU_SCOPE` and `OSDU_CLIENT_ID` under `Osdu:Partitions:<id>:Properties`, with
`OSDU_CLIENT_SECRET` as a Key Vault reference, and a local setup asks for the same five into `.sqlflow/env`. The flows
spell them as `${env:...}` references and are given them per partition. The explorer was reaching the same values the
long way round.

## Decision

- **The explorer reads a partition through the partition's own connection**: those five values, resolved as a run of the
  partition resolves them (the control plane's value for the partition, then its value for no partition, then the
  control plane's environment), with no repository's values, since the explorer belongs to no repository. The client
  signs in with the OAuth 2.0 client credentials grant, every request carries `data-partition-id`, and search, storage
  and schema are read at their own paths under the base URL.
- **The partition is a registered one**, settled as for any other read (the request's, the workbench's, the registry's
  default). One the registry does not hold is refused as a run in it is; one missing a value is told which, and where to
  set it.
- **A record page's check stays on the record's own flow.** The page reads the record through its flow's route and
  credentials, so the check of what it shows reads the same way.
- **One operation and one pool of connections serve both.** `delivery-explore` takes a target, made from a partition's
  connection or from a flow, and runs the same reads on either; the control plane keeps connections by target, so the
  explorer and a record page share the one a flow and its partition resolve alike.

## Alternatives rejected

- **Borrowing a flow's connection**, the design this replaces, for the reasons in the context: a view of OSDU that a
  fresh estate cannot use and whose credentials move with the catalog.
- **A connection setting of the explorer's own**, a section of the configuration naming the explorer's platform and
  client per partition. It would be a second copy of values every partition's configuration already carries, which a
  deployment would have to keep in step with the first; the five names are already the contract the templates, the local
  setup and the flows share.

## Consequences

- A deployment, or a local setup, can be searched as soon as its configuration names its partitions; no flow, sync or
  delivery is needed first.
- The explorer reads at the platform's default storage paths. A storage flow's `protocolOptions.verifyPath` applies to
  a record page's check alone.
- A partition that only a flow with credentials of other names reached has no explorer connection until the five values
  are set for it, and a partition only a header flow named is no longer read until it is registered.
- `GET /api/v1/delivery/explorer/connection` answers the `endpoint` the connection reaches in place of the flow it
  borrowed (`through`, `route`).
