---
id: delivery-concept-mapping-overview-sql
title: "The SQL of a table's rows: cross-checking a lookup join against the cache in SSMS"
type: concept
summary: "SQL in the overview's grid gives the T-SQL that reads the same rows in SSMS: a flow's table with its lookups joined, or a cached type's records."
keywords:
  - show the sql
  - sql of a table's rows
  - run it in ssms
  - copy into SSMS
  - cross-check a lookup
  - cross-check the join against the cache
  - query the cache tables
  - osdu.CacheItem
  - cache/items/sql
  - table-rows sql
  - lookup join query
related:
  - delivery-concept-mapping-overview
  - delivery-flow-delivery-lookups
  - delivery-concept-partition-cache
  - delivery-concept-value-proof
  - delivery-concept-api
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Source/SourceQuery.cs
  - osdu/src/SqlFlow.Delivery/Source/IngestionSql.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/TableRowsOperation.cs
  - osdu/src/SqlFlow.Delivery/Catalog/CacheProofSql.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/gui/src/features/delivery/overview/OverviewRows.tsx
  - osdu/gui/src/features/delivery/overview/MappingOverview.tsx
---

# The SQL of a table's rows: cross-checking a lookup join against the cache in SSMS

The grid of a table's rows under a [mapping's overview](mapping-overview.md#looking-at-a-tables-rows) has **SQL** in its
heading, and **Show the SQL** on a table's or a column's menu opens the grid on it. It shows, in place of the grid, the T-SQL
that reads the same rows in SSMS, its values declared at its top, ready to copy. Nothing is run here. The search typed over
the grid narrows the script as it narrows the grid, and **SQL** again goes back to the rows.

| Table | What the script reads | Where it runs |
| --- | --- | --- |
| The record table, or a child table | The rows the grid lists, in its order: the first 100 a delivery reads in the flow's scope, under the table's `filter` (a child row also under its record's), those marked deleted left out, every [lookup table](../flow/delivery-lookups.md) joined as a delivery joins it. Each value is whole, where the grid cuts one past 1,000 characters. | The flow's source database. |
| A cache table | The records the grid lists: the first 100 of the type in the version shown, by id, with a column per field they hold and the whole record last. | The catalog database, which holds the [partition cache](partition-cache.md). |

A search of OSDU is asked of OSDU's search, not of a database, and has no script.

## Cross-checking a lookup join against the cache

A lookup moved out of the cache into the flow's reads ([lookup tables joined into a delivery's reads](../flow/delivery-lookups.md))
is cross-checked by running both scripts. The child table's script gives each curve row the lookup's columns, under the names
the join gives them:

```sql
-- The first 100 rows of OsduData.silver.WellLogCurve that welldb-logs reads for the records in this scope, as the overview lists them, each value whole.
-- Runs on the flow's source database, as the tables hold them now.
-- Joined as a delivery joins them, by a LEFT OUTER JOIN on each key, a row marked deleted left out: CurveAlias (OsduData.silver.CurveAlias).
-- The scope: project
DECLARE @scope0 nvarchar(50) = N'NORTH-SEA';
-- The most rows to list.
DECLARE @page int = 100;

SELECT TOP (@page) t.[log_id], t.[curve_mnemonic], t.[curve_unit], t.[CurveAlias_curve_type], t.[CurveAlias_UpdatedDate_DW]
FROM (SELECT t.*, j0.[curve_type] AS [CurveAlias_curve_type], j0.[UpdatedDate_DW] AS [CurveAlias_UpdatedDate_DW] FROM [OsduData].[silver].[WellLogCurve] t LEFT OUTER JOIN [OsduData].[silver].[CurveAlias] j0 ON j0.[source_mnemonic] = t.[curve_mnemonic] COLLATE Latin1_General_CI_AS AND j0.[DeletedDate_DW] IS NULL) t INNER JOIN [OsduData].[silver].[WellLog] r ON t.[log_id] = r.[log_id]
WHERE r.[source_project] = @scope0 AND r.[DeletedDate_DW] IS NULL AND t.[DeletedDate_DW] IS NULL
ORDER BY r.[log_id], t.[curve_index];
```

The cache table's script lists what the cache holds of the type at the version shown, a field a column:

```sql
-- The first 100 records of CurveDictionary in version 20261110T061500Z of the cache of partition dev, as the overview lists them: each record's id, the fields it holds, and the whole record.
-- Runs on the catalog database.
DECLARE @scope nvarchar(200) = N'dev';
DECLARE @version nvarchar(64) = N'20261110T061500Z';
DECLARE @types nvarchar(max) = N'["CurveDictionary"]';
-- The most records to list.
DECLARE @rows bigint = 100;

SET NOCOUNT ON;
DECLARE @sequence int = (SELECT v.[Sequence] FROM [osdu].[CacheVersion] v WHERE v.[Scope] = @scope AND v.[Version] = @version AND v.[PrunedUtc] IS NULL);
IF @sequence IS NULL
    THROW 50000, N'The partition''s cache holds no such version, or its retention pruned its records, so nothing can be shown of it. Read the type''s records again in the overview to name the current version.', 1;
WITH [items] AS (
    SELECT i.[RecordId], i.[FieldsJson]
    FROM [osdu].[CacheItem] i
    WHERE i.[Scope] = @scope
      AND i.[TypeName] IN (SELECT t.[value] FROM OPENJSON(@types) WITH ([value] nvarchar(200) '$') t)
      AND i.[FromSequence] <= @sequence AND (i.[ToSequence] IS NULL OR i.[ToSequence] > @sequence))
SELECT TOP (@rows) i.[RecordId] AS [id],
    (SELECT j.[value] FROM OPENJSON(i.[FieldsJson]) j WHERE j.[key] = N'mnemonic') AS [mnemonic],
    (SELECT j.[value] FROM OPENJSON(i.[FieldsJson]) j WHERE j.[key] = N'curve_type') AS [curve_type],
    i.[FieldsJson] AS [fields]
FROM [items] i
ORDER BY i.[RecordId];
```

A row whose `CurveAlias_curve_type` differs from the `curve_type` the cache holds under its mnemonic, or is empty where the
cache holds one, is a row the two would render differently. Searching the same mnemonic in both grids before showing the
SQL narrows both scripts to it.

The cache's script reads the version it was written for, and keeps reading it after a refresh writes a newer one. Once the
cache's retention prunes that version, the script stops with error 50000 saying so, rather than answering with no rows; the
grid, read again, names the current version.

## The routes

| Route | What it answers |
| --- | --- |
| `POST /api/v1/delivery/flows/{pipelineId}/table-rows` | The rows of a flow's table, with `sql`: the script of the same rows. |
| `GET /api/v1/delivery/cache/items/sql` | For `scope`, `type`, and optionally `search`, `version` and `pageSize` (100 by default, at most 200): the script of the first page `GET /cache/items` lists with the same values, as `scope`, `version`, `typeName` and `sql`. `400` without a type, `404` for a version the cache does not hold, `410` for one its retention pruned. |

Both are reads ([API](api.md)): nothing is written, and nothing is run but the read the grid itself shows.
