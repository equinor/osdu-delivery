---
id: delivery-table-access-restriction
title: "osdu.AccessRestriction: the access restrictions registered per partition, which a record's ACL in OSDU can be replaced with"
type: table
summary: "One row per access restriction registered for a partition: its name, what it is for, and the groups a restricted record's ACL in OSDU becomes."
keywords:
  - osdu.AccessRestriction
  - access restriction table
  - restriction registry
  - restricted ACL
  - legal hold group
  - quarantine group
related:
  - delivery-concept-access-restriction
  - delivery-concept-database
  - delivery-table-record
  - delivery-table-partition
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Catalog/AccessRestrictionRegistry.cs
  - osdu/src/SqlFlow.Delivery/Ledger/AccessRestrictions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryAccessRestrictionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261105225617_RecordAccessRestrictions.cs
---

# osdu.AccessRestriction: the access restrictions registered per partition, which a record's ACL in OSDU can be replaced with

The access restrictions an administrator registered for each OSDU partition
([Restricting access](../concepts/access-restriction.md#the-registry)): a name operators pick it by, what it is for, and
the entitlements groups that become a restricted record's `acl.viewers` and `acl.owners`. A record restricted under one
names it in [`osdu.Record.RestrictionId`](record.md). Added by migration `RecordAccessRestrictions` (module version 1.65.0).

A restriction's groups never change once it is registered, since OSDU holds them on the records restricted under it: a
different set of groups is a restriction of its own. Only what it is for can be said again. It is removed only while no
record carries it, in one serializable transaction with the count that says so; no foreign key backs the record's column,
since adding one to the ledger would read every one of its rows under a schema lock as the migration ran.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `RestrictionId` | `int` identity | no | The restriction, as a record's `RestrictionId` names it. |
| `Partition` | `nvarchar(200)` | no | The data-partition-id the restriction's groups belong to: only records of that partition are restricted under it, and the registry has to hold the partition when it is registered. |
| `Name` | `nvarchar(100)` | no | What operators pick it by: a letter or digit, then letters, digits, spaces, `.`, `_` or `-`; unique within its partition whatever its case. |
| `Description` | `nvarchar(400)` | yes | What it is for (a legal hold, a quality quarantine), in the words of whoever registered it or said it last. |
| `Viewers` | `nvarchar(2000)` | no | The groups a restricted record's `acl.viewers` becomes, as a JSON array of data group emails: 1 to 20, each a data group storage accepts in an ACL. |
| `Owners` | `nvarchar(2000)` | no | The groups a restricted record's `acl.owners` becomes, as `Viewers` holds them. The flow's identity has to be a member of one of them (or a data manager) for a record to be restricted under it, since only an owner can give it its access back. |
| `CreatedUtc` | `datetime2` | no | When it was registered. |
| `CreatedBy` | `nvarchar(200)` | no | Who registered it. |
| `UpdatedUtc` | `datetime2` | no | When what it is for was last said. |
| `UpdatedBy` | `nvarchar(200)` | no | Who said it last. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_AccessRestriction` | `RestrictionId` | primary key | none | none | A restriction by its id, as a record names it. |
| `IX_AccessRestriction_Partition_Name` | `Partition`, `Name` | unique | none | none | One name per partition, whatever its case; a partition's restrictions listed by name, and the one a restriction run names found. |
