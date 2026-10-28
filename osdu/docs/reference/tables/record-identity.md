---
id: delivery-table-record-identity
title: "osdu.RecordIdentity: every value a record can be found by, folded to upper case for prefix lookups"
type: table
summary: "One row per value a record is known by (an identity column, a key value, a label word, its OSDU id, its file): what the record search reads."
keywords:
  - osdu.RecordIdentity
  - record identity table
  - record search
  - identity index
  - prefix search
  - find a record
  - dataset.identity
related:
  - delivery-concept-database
  - delivery-concept-ledger
  - delivery-table-record
  - delivery-flow-mapping
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/RecordIdentity.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/RecordSearchContributor.cs
---

# osdu.RecordIdentity: every value a record can be found by, folded to upper case for prefix lookups

One row per value a record is findable by: a value of the columns its mapping declares in `dataset.identity` (a wellbore
name, a UWI), its source key whole and each key value, up to 8 words of its label, its OSDU id and the part after the
last colon, and the ingestion file its current version came from. The token is folded to upper case, so one index answers
"starts with" for every identifier an operator may hold, across every flow of a partition and in one flow's listing
([The delivery ledger](../concepts/ledger.md#finding-records)).

A record contributes at most 24 values, each at most 200 characters; values shorter than 3 characters are not stored. Rows
belong to their record: they are rewritten as a set whenever it is staged, and deleted with it. Not pruned, and the table
does not grow with activity.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `PartitionId` | `smallint` | no | The partition the row belongs to, as [`osdu.LedgerPartition`](ledger-partition.md) numbers it: the first column of the key. |
| `Token` | `nvarchar(200)` | no | The value folded to upper case: what a lookup seeks. |
| `FlowId` | `uniqueidentifier` | no | The ledger identity of the record's flow. |
| `DeliveryKey` | `uniqueidentifier` | no | The record's delivery key ([`osdu.Record`](record.md)). |
| `Display` | `nvarchar(200)` | no | The value as it was read, for showing beside a hit. |
| `Kind` | `nvarchar(16)` | no | Where the value came from: `identity`, `key`, `label`, `osdu` or `file`. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_RecordIdentity` | `PartitionId`, `Token`, `FlowId`, `DeliveryKey` | primary key | none | none | The lookup: any identifier an operator holds, as a prefix, across the flows of a partition; the record it names is read by joining to the record's key. |
| `IX_RecordIdentity_PartitionId_FlowId_DeliveryKey` | `PartitionId`, `FlowId`, `DeliveryKey` | index | none | none | A record's own values, to rewrite or delete them when its identity changes. |
| `IX_RecordIdentity_PartitionId_FlowId_Token` | `PartitionId`, `FlowId`, `Token` | index | none | none | The same lookup narrowed to one flow (the Records page's flow filter), so a prefix many other flows share cannot use up the candidate bound before this flow is reached; and a flow's own listing searched by prefix (its Records tab, `sqlflow records list --search`). |

## Written and read by

- **Written** by the ledger whenever a record is staged, as the whole set of its values (`OsduLedger.cs`,
  `RecordIdentity.cs`), and deleted with the record.
- **Read** by the search box and `GET /api/v1/delivery/records?search=` (`RecordSearchContributor.cs`), which takes at
  most 1,000 candidates from the index and reports a larger match as a floor.
- **Rewritten** by a ledger move (`OsduLedger.Moves.cs`), which puts the rows of an old ledger under the
  identity of the flow that replaced it or took it over, a slice at a time ([Renaming a pipeline](../concepts/renaming-a-pipeline.md)).
