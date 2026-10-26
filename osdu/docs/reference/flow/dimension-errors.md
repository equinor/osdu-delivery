---
id: delivery-flow-dimension-errors
title: "Dimension flow errors: what is refused when the document is read, and when a dimension builds"
type: flow-reference
summary: "The errors of a dimension flow: each refusal when its document is read and each failure of a build, with its cause and what to do."
keywords:
  - dimension errors
  - refused
  - invalid YAML
  - maxValues exceeded
  - template not found
  - view refused
related:
  - delivery-flow-dimension
  - delivery-flow-dimension-attributes
  - delivery-flow-dimension-views
  - delivery-flow-dimension-build
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DimensionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionViewMapper.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
---

# Dimension flow errors: what is refused when the document is read, and when a dimension builds

What a [dimension flow](dimension.md) is refused for when it is read, and what fails a build. A label's and an attribute's settings have their errors on [Dimension labels and attributes](dimension-attributes.md#errors-when-the-document-is-read), and a view's on [Dimension views](dimension-views.md).

## Errors when the document is read

| Message (abridged) | Cause |
| --- | --- |
| `dimensions must list at least one dimension; a dimension flow without dimensions has nothing to build.` | No `dimensions`. |
| `dimensions[1] 'Well-Type' would share its table with 'Well.Type': a table's name keeps the letters, digits and underscores of a dimension's name, and theirs are the same. Rename one of them.` | Two names that make the same table name. |
| `path legal.foo is not a value the index holds of the record; under legal it holds legal.legaltags, legal.otherRelevantDataCountries, legal.status.` | A record property the indexer does not map. |
| `the key's column would be named 'id', after the property its path ends with, which is a column every dimension's table has already (id, partition, key_id, records, filter). Name it in the document: columns: { key: <name> }.` | `path: id` without `columns.key`. |
| `the value's column would be named 'FacilityName', after the property its label ends with, which is the name of its attribute FacilityName, ...` | An attribute named like a column. |
| `unlabelled is the value of a key whose label or attribute is not read, and the dimension reads neither.` | `unlabelled` without `label` or `attributes`. |
| `attributes.B is a second collected attribute beside A; a dimension collects one, ...` | Two `collect` attributes. |
| `label reads through 4 records; a label reads through at most 3, a search per step for every key.` | Too many steps. |
| `clean[0]: the pattern /(a)\1/ cannot be used: it uses something the non-backtracking engine cannot run ...` | A pattern needing backtracking. |
| `query uses '{logSource}', which is not declared under parameters.` | An undeclared token. |
| `source.headers names 'data-partition-id', and the flow names its partitions: every run sets the header to the partition it builds in. Remove the header.` | Both ways of naming a partition. |
| `source.aggregationSize is 20000; ... between 10 and 10000.` | Out of range. |
| `incremental names no keyColumns. An incremental load keeps the keys each record holds by the record's unique key, ... name it, keyColumns: [id] for an OSDU record.` | An `incremental` block without `keyColumns`. |
| `incremental.keyColumns names 4 properties; it names from 1 to 3.` | Too many, or none. |
| `incremental.dateColumns names modifyTime twice.` | A column named twice. |
| `incremental.lagMinutes is -1; ... from 0 to 1440 minutes.` | Out of range. |
| `incremental.fullLoadAfterHours is 0; ... from 1 to 8784. ...` | Out of range. |
| `views needs target.connection: the module's database as the pipelines reading the views name it ...` | Views without `target.connection`. |
| `views[0] 'Curve': join[0].on 'Mnemonic' holds no key: Mnemonic is a field kept as a value ...` | A join on a column holding no key. |
| `views[0] 'Curve': join[0] joins 'WellLogID' to RefUnitOfMeasure, and it holds the id of a work-product-component--WellLog and the dimension is keyed by the id of a reference-data--UnitOfMeasure, ...` | Keys that never meet. |
| `views[0] 'Curve': join[0] joins Wellbore, whose table holds a row per value it collects of each key, so a join to it would repeat the view's rows. ...` | A join to a dimension of several rows a key. |
| `views[0] 'Curve': columns.Interval: the expression 'BaseDepth - TopDepth' gives the operator - text (BaseDepth), where it takes a number; ...` | An implicit conversion. |
| `views[0] 'Curve': columns.Curve: the expression '...' uses a subquery, which a view's expression may not: ...` | Something an expression may not use. |
| `views[0] 'Curve': columns.Depth: dataType converts to 'varchar(10)'; varchar and char would turn every character outside their code page into ? ...` | A type a view does not convert to. |
| `views[0] 'PerLog': columns.Curve: the expression 'Mnemonic' names Mnemonic outside an aggregate, and the view groups its rows by WellLogID: ...` | A grouped view's column reading a column it does not group by. |
| `views[0] 'Curve': columns.Top: the expression 'MAX(TopDepth)' calls MAX, an aggregate of the rows of a group, where one row is read ...` | An aggregate in a view without `groupBy`, or in a `where`. |
| `views[0] 'PerLog': columns.Last: the expression '...' orders the rows of a group otherwise than columns.First does ...` | Two `STRING_AGG ... WITHIN GROUP` of one view ordered differently. |
| `views[0] 'PerLog': groupBy groups the view's rows, and a grouped view holds the columns it lists ...` | A grouped view that lists no column. |
| `views[1] 'Logs': join[0].view names 'LogAssurances', which is no view declared before this one in the flow ...` | A join to a view declared later, or to none. |
| `views[1] 'Logs': join[0] joins view LogAssurances, which a join cannot meet one row of: it groups no rows ...` | A join to a view that groups no rows, groups by several columns, or does not list its group column bare. |
| `views[1] 'Logs': join[0] names both to and view; ...` | A join naming both or neither. |

## Errors when a dimension builds

| Message (abridged) | What to do |
| --- | --- |
| `Dimension Wellbore reads data.WellboreID of <pattern>, and the flow reads no template of <kind> (<n> record(s)), so how the index stores the field there is not known: no template folder on the path of ... holds template <kind>. ...` | Put the template of every kind the pattern matches in a template folder on the flow's path (`sqlflow template capture --kind <kind> --out <folder>/<file>.json`), or narrow `kind`. |
| `Dimension <name> reads <path>, which the kinds its pattern matches index differently: ... One field is read one way; narrow the kind to the versions that agree.` | Narrow `kind`. |
| `<path> holds more than <n> distinct values, the most this dimension keeps (maxValues). Narrow the dimension's query, or raise maxValues if the partition really holds that many.` | Narrow or raise `maxValues`. |
| `Dimension <name> of <flow> would write the table osdu.dim_<name>, which dimension <name> of <other flow> writes: ... Rename one of them.` | Dimension names are unique across the flows of a database. |
| `Dimension <name> could not read dictionary <dictionary>: ...` | Add or fix `dictionaries/<name>.yaml`. |
| `The dimension's table osdu.dim_<name> has a column <name> already ... Drop that column, or name the <role>'s column otherwise in the flow: columns: { <role>: <name> }.` | A rename would take a name another column holds. |
| `Dimension flow '<flow>': target.connection ${env:<NAME>} reaches another database than this host's module database, ... Point it at the module's database.` | Point the reference at the module's database. Nothing was built. |
| `View <name> is declared by dimension flow '<other>' as well, and a view's name is unique among the flows of a database, so osdu.v_dim_<name> was not written. ...` | Rename one of the views, or remove the other flow's. |
| `osdu.v_dim_<name> is in the database, and no build of a dimension flow made it, so it is not written over. ...` | Drop the object, or name the view otherwise. |
| `View <name> joins osdu.v_dim_<other>, which this build could not write, so it was not written either and keeps its last definition; ...` | Fix what stopped the view it joins; the next build writes both. |
| `Column <column> of view <name> could not be computed for every row of partition '<partition>': Arithmetic overflow ...` | Write the expression around the value. |
| `incremental.keyColumns of dimension flow '<flow>', for dimension <name>, names <path>, which a record of <kind> can hold more than once, so it cannot stand for one record. ...` | Name properties each record holds once, such as `id`. |
| `incremental.dateColumns of dimension flow '<flow>' names <path>, which the index holds as text, not a date, so no window can be read on it. ...` | Name dates, such as `modifyTime` and `createTime`. |
| `Dimension flow '<flow>' declares no incremental block, so every build loads its dimensions in full and a backfill window has nothing to bound. ...` | Declare `incremental`, or run without `--from` and `--to`. |
| `The records of dimension <name> hold more than 20,000,000 keys between them, more than a load keeps by record for incremental loads. ...` | Narrow its query, split it, or take out `incremental`. |
| `The staging of dimension build <id> holds <n> of the <n> key(s) and <n> of the <n> value(s) it staged: another build of the dimension deleted it, taking this build, running for more than 48 hours, for one its process left. Nothing was written; build the dimension again.` | The build ran for more than 48 hours, and a later build of the dimension took it for one whose worker stopped ([What a build holds](dimension-build.md#what-a-build-holds)). Build the dimension again. |
