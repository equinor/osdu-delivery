---
id: delivery-table-partition
title: "osdu.Partition: the partition registry, the OSDU partitions registered with the catalog and the default one"
type: table
summary: "One row per OSDU data partition registered with the catalog: what it is for, whether it is the default, whether the configuration names it, and who changed it."
keywords:
  - osdu.Partition
  - partition table
  - partition registry
  - data-partition-id
  - default partition
  - "Osdu:Partitions"
  - configured partitions
related:
  - delivery-concept-database
  - delivery-concept-partitions
  - delivery-cli-partition
  - delivery-table-config-property
  - delivery-table-ledger-partition
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryPartitionRegistry.cs
  - osdu/src/SqlFlow.Delivery/Catalog/ConfiguredPartitions.cs
  - osdu/src/SqlFlow.Delivery/Model/RegisteredPartitions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryPartitionEndpoints.cs
---

# osdu.Partition: the partition registry, the OSDU partitions registered with the catalog and the default one

The partition registry: the OSDU partitions registered with the catalog, and the one marked default
([The partition registry](../concepts/partitions.md#the-partition-registry)). A flow that names no partitions serves every
registered one, so the same documents deploy to every environment, and a run that names none runs in the default. A
deployment fills the registry from the control plane's configuration (`Osdu:Partitions`), which the control plane writes
into it when it starts ([Configured partitions](../concepts/partitions.md#configured-partitions)); a partition can also be
registered by hand with `sqlflow partition` or `POST /api/v1/delivery/partitions`. This is not the ledger's numbering of
partitions, which is [`osdu.LedgerPartition`](ledger-partition.md).

The first partition registered becomes the default, and the default can be removed only when it is the last. Removing a
partition deletes nothing kept under it.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Name` | `nvarchar(200)` | no | The data-partition-id, as runs name it and caches and ledgers are keyed by it. |
| `Description` | `nvarchar(400)` | yes | What the partition is for (the environment, the platform), in the words of whoever registered it. |
| `IsDefault` | `bit` | no | Set for the one partition a run that names none runs in. |
| `IsConfigured` | `bit` | no | Set while the control plane's configuration (`Osdu:Partitions`) names the partition: the control plane registered it from there, and only the configuration describes it, makes it the default or takes it out. A partition the configuration stops naming stays registered with this cleared. |
| `CreatedUtc` | `datetime2` | no | When it was registered. |
| `CreatedBy` | `nvarchar(200)` | no | Who registered it. |
| `UpdatedUtc` | `datetime2` | no | When it was last changed. |
| `UpdatedBy` | `nvarchar(200)` | no | Who changed it last. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Partition` | `Name` | primary key | none | none | A partition by its name. |
| `IX_Partition_IsDefault` | `IsDefault` | unique | `[IsDefault] = 1` | none | At most one partition is the default. |

## Written and read by

- **Written** by the control plane's start from its configuration (`ConfiguredPartitions.cs`), by `sqlflow partition`, and
  by the Partitions page through the partition API (`POST /api/v1/delivery/partitions`, `PUT` and `DELETE
  /api/v1/delivery/partitions/{name}`, `POST /api/v1/delivery/partitions/{name}/default`) (`DeliveryPartitionRegistry.cs`,
  `DeliveryPartitionEndpoints.cs`).
- **Read** by every run that resolves its partition, the repository sync, the Partitions page and the partition API.
