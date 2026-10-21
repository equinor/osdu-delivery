---
id: delivery-table-ledger-partition
title: "osdu.LedgerPartition: the number each OSDU partition is keyed by in every ledger table"
type: table
summary: "One row per data partition the ledger has rows in: the two-byte number every ledger key starts with, and the partition's name."
keywords:
  - osdu.LedgerPartition
  - ledger partition table
  - partition number
  - PartitionId
  - data-partition-id
  - ledger keys
related:
  - delivery-concept-database
  - delivery-concept-ledger
  - delivery-concept-partitions
  - delivery-table-ledger
  - delivery-table-partition
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Directory.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20260928064320_LedgerPartitions.cs
---

# osdu.LedgerPartition: the number each OSDU partition is keyed by in every ledger table

One row per OSDU data partition the ledger keeps rows under. Every ledger table's key starts with `PartitionId`, a
two-byte number, rather than the partition's name: a name of up to 200 characters carried in every key of the record
table would push the source file index past SQL Server's 1,700-byte key limit and widen every index of the largest
tables. So the ledger keys by the number and names the partition here, and one partition's rows are one range of every
clustered index ([The delivery ledger](../concepts/ledger.md#ledgers-flows-and-partitions)).

A row is added the first time a ledger of the partition is registered, and is never renumbered or removed. The number
`0` is reserved for a ledger the upgrade to partition keys could not place; no row carries it, and that ledger's next
run moves its rows into the partition it delivers to. This table is not the partition registry: the partitions a
deployment delivers to are [`osdu.Partition`](partition.md).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` identity | no | The number every ledger key starts with. Assigned when the partition is first registered; the table holds at most 32,767 partitions, and a registration past that is refused. |
| `Name` | `nvarchar(200)` | no | The data-partition-id, as runs name it. |
| `CreatedUtc` | `datetime2` | no | When the partition was numbered, in UTC. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_LedgerPartition` | `PartitionId` | primary key | none | none | The partition's name for a number a ledger row carries. |
| `IX_LedgerPartition_Name` | `Name` | unique | none | none | The number of a partition a run names. A partition has one number whatever case it is written in: the registry compares names regardless of case, and so does the index under SQL Server's default collation. |

## Written and read by

- **Written** by the ledger directory (`OsduLedger.Directory.cs`) when a run registers a ledger in a partition that has
  no number yet. Two registrations racing for the same partition both end with the one number the unique index allowed.
- **Read** by every ledger read and write that turns a partition name into its number or back, by the partition and
  pipeline endpoints of the control plane, and by the dimension view store.
