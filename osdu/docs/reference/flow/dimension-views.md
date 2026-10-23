---
id: delivery-flow-dimension-views
title: "Dimension views (views): dimensions of one flow joined into one table, typed, written and checked on every build"
type: flow-reference
summary: "A dimension flow's views: the target and views keys, joins and typed columns, the osdu.v_dim_ view a build writes and checks, and what templates say of joins."
keywords:
  - views
  - join dimension tables
  - typed columns
  - "osdu.v_dim_"
  - target.connection
  - datatype
  - conversions
  - view check
  - template check
  - join agrees differs unchecked
  - suggest joins
related:
  - delivery-flow-dimension
  - delivery-concept-dimension-tables
  - delivery-cli-dimensions
  - delivery-guide-dimensions
  - delivery-concept-templates
  - concept-schema-evolution
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DimensionViewMapper.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionViewSpec.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionViews.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionViewExpressions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionViewRemoval.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionViewTemplates.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionViewStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionTables.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryDimensionViewEndpoints.cs
  - osdu/gui/src/features/delivery/dimensions/DimensionViewTabs.tsx
  - osdu/gui/src/features/delivery/dimensions/DimensionViewSuggest.tsx
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261009204033_DimensionViews.cs
---

# Dimension views (views): dimensions of one flow joined into one table, typed, written and checked on every build
A view puts dimensions of one flow side by side at the grain of one of them, so a pipeline, a report or a person reads
one table instead of writing the joins. A build writes each as `osdu.v_dim_<name>` after the flow's dimensions, checks it,
and drops a view the flow no longer declares.

```yaml
target:
  connection: ${env:SQLFLOW_CATALOG_DB}   # the module's database, the catalog's, named as the pipelines reading the views name it
views:
  - name: Curve                           # osdu.v_dim_Curve
    description: Every curve of every well log, with its log and the curve's unit.
    from: LogCurve                        # a row of the view for each row of dim_LogCurve
    join:
      - { on: WellLogID, to: WellLog, as: Log }       # the same key: the log's own row
      - { on: CurveUnitID, to: RefUnitOfMeasure, as: Unit }
      - { on: Log.SamplingDomainTypeID, to: RefSamplingDomainType, as: Domain }   # through the log
    columns:
      WellLogID: WellLogID
      Log: Log.WellLogName
      Curve: Mnemonic
      Unit: Unit.UnitCode
      Domain: Domain.SamplingDomainTypeName
      TopDepth: { expression: TopDepth, dataType: float }
      Interval: { expression: "TRY_CAST(BaseDepth AS float) - TRY_CAST(TopDepth AS float)", dataType: "decimal(18,3)" }
      Created:
        expression: Log.CreationDateTime
        dataType: datetime2(0)
        description: When the log was made, in UTC.
```

| Key | Default | Meaning |
| --- | --- | --- |
| `target.connection` | required with `views` | The module's database, written as a delivery flow's `source.connection` is: a `${env:...}` or `${keyvault:...}` reference, or a connection string whose password is one. |
| `views[].name` | required | A letter, then letters, digits and underscores, at most 64; unique among the views of a database, ignoring case. |
| `views[].description` | none | What the view holds, shown with it. |
| `views[].from` | required | The dimension of the flow whose rows the view's rows are. |
| `views[].join[]` | none | `{ on, to, as }`: the column joined on (bare for a column of `from`, `alias.column` for one of an earlier join), the dimension of the flow joined, and the alias its columns are read by (the dimension's name unless given). At most 32. |
| `views[].columns.<name>` | every column | An expression, or `{ expression, dataType, description }`. At most 256, beside `partition` and `id`. |
| `views[].where` | every row | The condition a row of `from` is kept by: a T-SQL condition over the same columns, operators and functions as a column's expression, such as `element IS NOT NULL`. A value alone is refused. |

**What a view holds.** Every view begins with `partition` and `id`, the number of the `from` row each of its rows stands
for, which stays the same for as long as the dimension holds the row: a pipeline merges on it. Then its `columns`, in
order. A view that lists none holds every column of `from` but its numbers, partition and filter, then every column of
each join prefixed by its alias (`Unit_UnitCode`, `Unit_id`), as the tables hold them. Every join is a left join that
meets at most one row, so a row of `from` is a row of the view, unless its `where` leaves the row out. The check reads
the rows the view holds, its `where` applied.

**Joins**, refused where the document is read, naming the view and the join:

- A join is on a key: a dimension's key column, or an attribute or an element's field kept as `key` or `id`
  ([keeping a key](dimension.md#labels-and-attributes)). A key's column or a `keep: key` column joins a dimension keyed by the same
  path; a `keep: id` column joins a dimension keyed by `id`; two dimensions keyed by `id` read kinds of one entity type.
- A dimension with `elements` or a collected attribute holds several rows a key, and is refused as a join: it would
  repeat the view's rows. Make it the view's `from`.
- Joins stay inside one flow, so one run settles every table a view reads. Each compares the partition, the key's
  SHA-256 (`key_hash`, which every dimension table keeps and indexes) and the key's text under a binary collation, so
  ids that differ only in case never meet, whatever the database's collation.

**Expressions.** A column is a T-SQL scalar expression, read by SQL Server's own parser and written into the view by the
module from what it read. It uses numbers, `'text'`, `N'text'`, `NULL`, `+ - * / %`, comparisons, `AND`, `OR`, `NOT`,
`IS NULL`, `LIKE`, `IN` a list, `BETWEEN`, `CASE`, and the functions `COALESCE`, `NULLIF`, `ISNULL`, `IIF`, `TRY_CAST`,
`TRY_CONVERT`, `CAST`, `CONVERT` (no style), `LEFT`, `RIGHT`, `SUBSTRING`, `LEN`, `UPPER`, `LOWER`, `TRIM`, `LTRIM`,
`RTRIM`, `REPLACE`, `CHARINDEX`, `CONCAT`, `CONCAT_WS`, `ABS`, `ROUND`, `FLOOR`, `CEILING`, `POWER`, `SQRT`, `EXP`, `LOG`,
`LOG10`, `SIGN`, `DATEADD`, `DATEDIFF`, `DATEPART`, `YEAR`, `MONTH`, `DAY` and `EOMONTH`. A subquery, a variable, a
window, `COLLATE`, a value of the clock (`GETDATE()`) and any other function are refused. Every column of a dimension's
table is text but `id`, `key_id`, `records` and `element`; where SQL Server would convert implicitly (`BaseDepth -
TopDepth`, text compared with a number, `CONCAT` of a number) the expression is refused, saying to convert first. A
division by zero, a root or a logarithm outside its domain and a negative length are null. Quote an expression that
starts with `[`, and a type with a comma in a flow mapping (`dataType: "decimal(18,3)"`).

**Data types.** `dataType` converts by the module's conversion, which never fails a read: a value it cannot convert is
null, and the build counts it. The types are `bit`, `tinyint`, `smallint`, `int`, `bigint`, `decimal(p,s)`,
`numeric(p,s)`, `float`, `real`, `date`, `time(n)`, `datetime2(n)`, `datetimeoffset(n)`, `uniqueidentifier`,
`nvarchar(n)` and `nvarchar(max)`; `varchar`, `char`, `datetime` and `smalldatetime` are refused. The same conversion
runs for `CAST`, `CONVERT`, `TRY_CAST` and `TRY_CONVERT` inside an expression.

| To | Reads | Unlike `TRY_CAST` |
| --- | --- | --- |
| `float`, `real` | `203.149`, `-999.25`, `1.5E3`; not `12,5` or `NaN` | The same. |
| `decimal`, `numeric` | The same, rounded to its scale | Reads an exponent (`1.5E3`), through `float`. |
| `tinyint` to `bigint` | A whole number: `7`, `7.0`, `7E0` | Reads `7.0`; a fraction (`7.5`) is null, never cut. |
| `bit` | `true`, `false`, `1`, `0`, ignoring case | `2` and `yes` are null. |
| `datetime2(n)` | ISO 8601, as the instant in UTC (`11:16:03+02:00` is `09:16:03`); no offset is UTC | Applies the offset, which a cast drops; reads ISO 8601 alone, whatever the session's date format (`03/04/2013` is null). |
| `datetimeoffset(n)`, `date`, `time(n)` | ISO 8601, keeping the offset, or the date or time of day as written | ISO 8601 alone. |
| `nvarchar(n)` | A text of at most `n` characters; a number with every digit, a date as ISO 8601 | A longer text is null, never cut. |
| `uniqueidentifier` | A GUID's text | The same. |

**The check.** After writing its views, a build reads each one's rows of the run's partition: the rows, for each join
how many rows found theirs and some values that found none, and for each converted column how many values did not
convert, with examples (the row's `id` and the text). The run's result says so (`CurveUnitID: 1,204 of 88,310 rows name
no row of RefUnitOfMeasure`, `TopDepth: 37 value(s) did not convert, for example '12,5'`). A value no expression could be
written around (an arithmetic overflow) fails the build, naming the view and the column, so the pipelines ordered after
the flow do not read a view that cannot be read.

**Writing.** A build writes each view in one transaction under a lock of its own, only when its statement changed, with
its column list (never `*`), making a table it reads that no build has made yet (empty, so its join finds nothing). A
view whose name another flow's view holds, or an object no build made, is refused, naming it, and nothing is written
over. A build that renames a key's or a value's column drops the views reading that table in the same transaction, and
the run writes them again at its end. A dimension a view reads is not removed until the flow's next build has written
the view without it.

**Lineage.** With `target.connection`, the flow declares that it writes each `osdu.dim_<dimension>` table and each
`osdu.v_dim_<view>` view on that connection, so a pipeline reading them through the same reference is ordered after it.
SQLFlow tells servers apart by the reference as written, so the two name it alike. A build and a plan ask the server
which database the reference reaches and refuse one other than the module's own, naming the reference and never what it
resolves to. A table of facts for analytical queries is an ingestion flow reading the view (keyed by `partition` and
`id`) into a table with a clustered columnstore index:

```yaml
flowType: ing
name: welldb-welllog-08-curve-table-ing
connections:
  osduDelivery: ${env:SQLFLOW_CATALOG_DB} # the same reference as the dimension flow's target.connection
  warehouse: ${env:WAREHOUSE_DB}
source:
  server: osduDelivery
  object: OsduDelivery.osdu.v_dim_Curve
target:
  server: warehouse
  object: Warehouse.arc.Curve
  columnStoreIndex: true                  # made when the run first creates the table
load:
  keyColumns: [partition, id]
  matchKeysInSourceAndTarget: true
matchKeys:
  action: delete                          # a curve the view no longer holds leaves the table
```

## What the templates say of a view's joins

A join is declared, never inferred, and a build writes a view from its document alone. Beside that, a view's page reads
the templates the template folders on its flow's path hold ([template folders](../concepts/templates.md#template-folders)),
as the flow's repository last synced them, and says of each join its flow declares now whether the column joined on names records of the type
the dimension joined reads, since an id carries its entity type and a join between two types finds nothing. It is
advice: it never stops a build or changes what one writes. `GET /api/v1/delivery/dimensions/views/{name}` gives it as
`joinChecks`, one per join in order:

| Field | Holds |
| --- | --- |
| `alias`, `on`, `to` | The join's alias, the column joined on as the document writes it, and the dimension joined. |
| `names` | The entity types the column's values name, as the template of the records it is read from declares them (`x-osdu-relationship`); for the key column of a dimension keyed by `id`, the entity type its kind names. Empty where nothing says. |
| `reads` | The entity type the joined dimension's kind names, for a dimension keyed by `id`; null for one keyed by another path, or one whose kind names any type. |
| `verdict` | `agrees`, `differs` or `unchecked`. |
| `note` | The verdict as a sentence, such as `The template says CurveUnitID names reference-data--UnitOfMeasure, which RefUnitOfMeasure reads.` |

The verdict is settled by what the column joined on holds:

| The column joined on | Verdict |
| --- | --- |
| The key column of a dimension keyed by a path, or a column kept as `key` | `agrees`: it holds the text the joined dimension is keyed by (the document already refused a join to a dimension keyed by another path). |
| The key column of a dimension keyed by `id` | Its kind's entity type against the joined dimension's: `agrees` when they are the same, `differs` when not, `unchecked` when either kind names any type. |
| A column kept as `id` | The template the flow reads of the kind the holding dimension reads (of the kinds its pattern matches, the newest) names the entity types the column's path refers to: `agrees` when they include the type the joined dimension reads, `differs` when they do not, `unchecked` when the joined dimension's kind names any type, when the template does not declare the path a reference, or when no template the flow reads describes the path. |
| A join to a dimension the flow does not declare now, or on an alias no earlier join gives | `unchecked`. |

A template describes the path of a column kept as `id` only where the column is read from the dimension's own records:
an element's field (the element's path, less `up` segments, then the field's path), a collected attribute's path, and an
attribute of a dimension keyed by `id` read in one step. An attribute read through the records a key names is
`unchecked`, with `No template the flow reads of <kind> says which records <column> names, so the join is not checked.
Put the template in a template folder on the flow's path to check it.`

The view's page shows the verdicts in three places: the **Joins** tab's **Template** column, a glyph beside each join on
the **Definition** tab (the sentence, `names` and `reads` on hover), and, when any join differs, `<n> join(s) differ
from the template` in the view's summary, which opens the Joins tab.

The same reading offers joins a view could make: **Suggest joins** on the views list,
`GET /api/v1/delivery/flows/{pipelineId}/dimensions/views/suggest?from=<dimension>`, and
[`sqlflow dimensions views --suggest`](../cli/dimensions.md#views). Each column of `from` holding a key is offered a join to
every other dimension of the flow it meets that holds one row a key: always where the two hold the text of one path,
and for a column of ids only where the verdict is `agrees`. Then the same through each dimension so joined, one level
further, each alias made unique with a number. Nothing is written; the answer is a `join:` block to keep or change.
