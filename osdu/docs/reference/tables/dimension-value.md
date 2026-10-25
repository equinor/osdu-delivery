---
id: delivery-table-dimension-value
title: "osdu.DimensionValue: one key of a dimension, exactly as the index holds it, and the value it is under"
type: table
summary: "One row per original (key) of a dimension: the key exactly as the index holds it, its member or why it has none, its label, filter and records."
keywords:
  - osdu.DimensionValue
  - dimension value table
  - original
  - dimension key
  - label
  - left out
  - key_id
related:
  - delivery-concept-database
  - delivery-concept-dimension-tables
  - delivery-flow-dimension
  - delivery-table-dimension-member
  - delivery-table-dimension-attribute
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
---

# osdu.DimensionValue: one key of a dimension, exactly as the index holds it, and the value it is under

One original of a [dimension](dimension.md): a key exactly as the index holds it, the member (clean value,
[`osdu.DimensionMember`](dimension-member.md)) it belongs to or why it belongs to none, and how many records (or objects
of a nested array) hold it. The tables keep the names they were made with: an original is a key. A row of the dimension's
table is a key the dimension holds now under its value, and its `key_id` is this row's `ValueId`
([The tables a dimension keeps](../concepts/dimension-tables.md)). A key no build finds is marked removed and keeps its
number should it come back.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `ValueId` | `bigint` identity | no | The original's id, unique on its own: what attributes, elements, records and changes name it by, and the `key_id` of its row in the dimension's table. |
| `DimensionId` | `int` | no | The dimension ([`osdu.Dimension`](dimension.md)). |
| `Original` | `nvarchar(1024)` | no | Collation `Latin1_General_100_BIN2`. The key exactly as the index holds it, at most 1,024 characters. |
| `OriginalHash` | `binary(32)` | no | SHA-256 of the original's UTF-8 bytes: what an original is unique by, exactly, however long it is. |
| `MemberId` | `bigint` | yes | The member it belongs to, or null when cleaning left it out of every member. |
| `LeftOut` | `nvarchar(16)` | yes | Why it belongs to no member: `empty`, `tooLong`, `dropped` or `failed`; null when it has a member. |
| `Note` | `nvarchar(400)` | yes | What cleaning had to say about it. |
| `Label` | `nvarchar(1024)` | yes | The label the dimension read for it from the record it names (`Wellbore A-1` for a wellbore's id), before cleaning; null for a dimension that reads no label, or when the build could not read one. |
| `LabelFrom` | `nvarchar(1024)` | yes | The id of the record the label was read from: the record the original names, or the last one a label's steps reached. |
| `Filter` | `nvarchar(4000)` | yes | The search filter finding the records holding it, exactly as the index holds it; null when no query can carry it. |
| `Count` | `bigint` | no | The records (or objects of a nested array) holding it. |
| `Filterable` | `bit` | no | Whether a query can carry it, so its member's filter finds its records. |
| `FirstSeenRunId` | `bigint` | no | The build that first found it. |
| `FirstSeenUtc` | `datetime2` | no | When that build ran. |
| `RemovedRunId` | `bigint` | yes | The build that no longer found it, or null while it is there. |
| `RemovedUtc` | `datetime2` | yes | When that build ran. |
| `MemberSinceRunId` | `bigint` | no | The build since which it belongs to its member, or to none. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_DimensionValue` | `PartitionId`, `DimensionId`, `ValueId` | primary key | none | none | A dimension's keys together, in the order they were written: every statement over one dimension seeks its own rows and locks no other's, so a build of another dimension writes beside it. |
| `IX_DimensionValue_ValueId` | `ValueId` | unique | none | none | An original named by its id. |
| `IX_DimensionValue_PartitionId_DimensionId_OriginalHash` | `PartitionId`, `DimensionId`, `OriginalHash` | unique | none | none | An original is one value of one dimension, once, compared by its hash so no length or collation blurs two. |
| `IX_DimensionValue_PartitionId_DimensionId_MemberId` | `PartitionId`, `DimensionId`, `MemberId` | index | none | none | A member's originals. |
| `IX_DimensionValue_PartitionId_DimensionId_Count_ValueId` | `PartitionId`, `DimensionId`, `Count` DESC, `ValueId` | index | none | none | The originals most records hold first, a page at a time. |

## Written and read by

- **Written** by a build's merge, in one transaction under the dimension's lock (`SqlServerDimensionStore.cs`).
- **Read** by the dimension's keys page, `sqlflow dimensions`, a composed search, a cache flow capturing the dimension,
  an incremental load, and the build that lays out the dimension's table.
