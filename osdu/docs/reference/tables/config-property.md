---
id: delivery-table-config-property
title: "osdu.ConfigProperty: the central configuration the control plane supplies to the runs it queues"
type: table
summary: "One row per configuration value: the name a flow spells as ${env:NAME}, its value or reference, and the repository and partition it applies to."
keywords:
  - osdu.ConfigProperty
  - config property table
  - central configuration
  - sqlflow config
  - env references
  - per partition values
  - legal tag
related:
  - delivery-concept-database
  - delivery-cli-config
  - delivery-concept-partitions
  - delivery-concept-environment-variables
  - delivery-table-partition
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryConfigStore.cs
  - osdu/src/SqlFlow.Delivery/Catalog/ConfiguredPartitions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/CentralConfigurationReferences.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/ConfiguredRunDispatcher.cs
---

# osdu.ConfigProperty: the central configuration the control plane supplies to the runs it queues

One property of the central configuration: a value the control plane supplies to the runs it queues, so a flow that names
`${env:NAME}` resolves it from here rather than from whatever the node that picks the run up holds
([`sqlflow config`](../cli/config.md)). A property is set for the whole control plane (`RepoId` null), and may be set
again for one repository and for one partition. For a run bound to partition P, a name's value is taken from the first
scope that sets it: the repository's for P, the control plane's for P, the repository's, the control plane's; a value set
for a partition always wins, so an estate-wide endpoint never sends a run of one partition to another's platform
([Environment variables](../concepts/environment-variables.md)). A value is a non-secret value or an `${env:NAME}` or
`${keyvault:vault/secret}` reference, which travels unresolved and is resolved on the node: the row and the run payload it
is carried in are ordinary catalog content, so a literal secret here is a defect. Not a ledger table: it has no
`PartitionId`.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Id` | `uniqueidentifier` | no | The property's id. |
| `RepoId` | `uniqueidentifier` | yes | The repository whose flows this value applies to, or null for the control plane's own value. |
| `Partition` | `nvarchar(200)` | yes | The OSDU partition whose runs this value applies to, by its data-partition-id, or null for a value that applies whatever the partition. |
| `Name` | `nvarchar(64)` | no | The reference name a flow spells as `${env:NAME}`, held as written. |
| `Value` | `nvarchar(1000)` | no | The value, or a reference the node resolves. |
| `Description` | `nvarchar(400)` | yes | What this property is for, shown wherever it is listed. |
| `IsConfigured` | `bit` | no | Set while the control plane's configuration (`Osdu:Partitions:<partition>:Properties`) sets this value: the control plane wrote it from there, and only the configuration changes or removes it. A value the configuration stops setting stays with this cleared. |
| `UpdatedUtc` | `datetime2` | no | When it was last set. |
| `UpdatedBy` | `nvarchar(200)` | no | Who set it last. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_ConfigProperty` | `Id` | primary key | none | none | A property by its id. |
| `IX_ConfigProperty_RepoId_Partition_Name` | `RepoId`, `Partition`, `Name` | unique | none | none | One value per name per scope: the control plane's own value is the row with no repository, and a value for no particular partition the row with no partition. Unfiltered, since SQL Server treats nulls as equal in a unique index, so one index covers every scope. |
| `IX_ConfigProperty_Name` | `Name` | index | none | none | A property's values in every scope. |

## Written and read by

- **Written** by `sqlflow config set` and `remove`, `PUT` and `DELETE /api/v1/delivery/config/{name}`, and the control
  plane's start from its configuration (`DeliveryConfigStore.cs`, `ConfiguredPartitions.cs`).
- **Read** by the control plane, which supplies the values set for no partition to every run it queues, of every kind
  (a pre-ingestion or ingestion flow's as much as a delivery's), and lays a partition's own values over them for the
  module's kinds (`CentralConfigurationReferences.cs`, `ConfiguredRunDispatcher.cs`), and by `sqlflow config`.
