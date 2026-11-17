---
id: flow-rpt-views
title: "Report views: show a query's answer as figures, a bar chart, a line or a table (queries.<query>.show, columns)"
type: flow-reference
summary: "How a report shows a query's answer: the forms of show, each view type and its keys, a click that sets a parameter, and how columns are labelled and written."
keywords:
  - report view
  - show
  - show a query as a bar chart
  - bar chart
  - column chart
  - line chart
  - figure
  - share
  - table
  - matrix
  - selects
  - click a bar to filter
  - columns
  - format
  - bytes
  - percent
  - duration
  - width
  - tile
  - flowtype rpt
yamlPath: "queries.<query>.show"
related:
  - flow-rpt
  - concept-reports
  - guide-write-a-report
sourceRefs:
  - src/SqlFlow.Yaml/YamlReportFlowLoader.cs
  - src/SqlFlow.Core/Reports/ReportFlow.cs
  - samples/reports/staging-overview.yaml
---

# Report views (queries.<query>.show, columns)

A query of a [report](rpt.md) is shown as one or more views of its answer, and each view is a tile on the report's page. `show` says which views. `columns` says how the answer's columns are labelled and written, in every view of the query.

Neither changes what the statement asks: a view draws the rows the statement answers.

## Example

```yaml
queries:
  by_source:
    title: Rows by source
    sql: |
      SELECT Source, COUNT(*) AS Staged, SUM(Bytes) AS Size
      FROM stg.Orders
      GROUP BY Source
      ORDER BY Staged DESC
    show:
      - { type: bar, by: Source, values: Staged, top: 10, selects: source }
      - table
    columns:
      Staged: { label: Rows staged, format: integer }
      Size: bytes
```

| Line | Effect |
| --- | --- |
| `show:` with two entries | Two tiles that draw the one answer. |
| `type: bar` | Horizontal bars, one per row, in the order the statement answers them. |
| `by: Source` | Each bar is named by the row's `Source`. |
| `values: Staged` | Each bar is as long as the row's `Staged`. |
| `top: 10` | Ten sources keep a bar each, and the rest are folded into one. |
| `selects: source` | A click on a bar sets the report's parameter `source` to that bar's `Source`, which runs again every query that binds `@source`. A second click clears it. |
| `- table` | The same rows as a table. |
| `Staged: { label: ..., format: integer }` | The column is headed **Rows staged** and written with digit grouping, in both tiles. |
| `Size: bytes` | The byte count is written in the largest unit that fits. |

## The forms of show

| Form | Example | Meaning |
| --- | --- | --- |
| Left out | | One `auto` view. |
| A view type | `show: bar` | One view of that type, with everything else left to the view. |
| A mapping | `show: { type: bar, by: Source }` | One view, with its keys. `type` is required. |
| A list | `show: [figure, { type: line, by: Day }]` | Several views of the one answer, in order. Each entry is a view type or a mapping. At most 6. |
| `none` | `show: none` | The query is not shown. It only feeds a parameter's choices (`choicesFrom`). |

A `matrix` needs `across`, so it is always written as a mapping.

## View types

| Type | What it draws | Keys it takes |
| --- | --- | --- |
| `auto` | A view chosen from the answer's shape: figures for a single row of up to six values, a line for rows over one date column with numbers, bars for rows of one name and one number, otherwise a table. | |
| `figure` | One large figure per value of the first row. | `values` |
| `bar` | Horizontal bars, one per row, in the order the query answers. | `by`, `values`, `series`, `stacked`, `top`, `selects` |
| `column` | Vertical columns, one per row, in the order the query answers: for buckets that have an order. | `by`, `values`, `series`, `stacked`, `selects` |
| `line` | A line over an ordered column, usually a date. | `by`, `values`, `series` |
| `share` | One bar divided into the parts of a whole. | `by`, `values`, `top`, `selects` |
| `table` | The rows as they are. | `by`, `values`, `selects` |
| `matrix` | A grid of one value by two names: rows down, columns across. | `by`, `across`, `values`, `selects` |
| `none` | Nothing. | No key at all. |

Every type but `none` also takes `title`, `description` and `width`.

## View keys

| Key | Type | Description |
| --- | --- | --- |
| `type` | string | The view type, in lower case. |
| `title` | string | The view's own heading, when it is not the query's. At most 200 characters. |
| `description` | string | What the view shows, when it is not the query's description. At most 1000 characters. |
| `width` | string | How wide the tile is: `quarter`, `third`, `half`, `two-thirds` or `full`, of the page's width. Left out, figures, a table and a matrix take the full width, and a bar, column, line or share view half; an `auto` view takes the width of the form it turns out as. On a narrow page every tile takes the full width. |
| `by` | column | The column that names each mark: a bar's or a share's category, a line's or a column chart's position, a matrix's rows. Left out, the first column that fits. With `selects`, the column the clicked value is read from, and required. |
| `values` | column, or a list of columns | The columns drawn as values (figures, bars, lines), or the columns a table lists. Left out, every column that fits. A `share` and a `matrix` show one value. |
| `series` | column | The column whose values split one value into several series: stacked bars, or a line each. Goes with one column under `values`. |
| `across` | column | A matrix's columns: the column whose values are laid out across. Required on a `matrix`. |
| `stacked` | bool | Whether several values or series are stacked on one bar. Left out, series are stacked and several values are set side by side. |
| `top` | integer | How many categories a view shows at first. A `bar` view lists that many rows and keeps the rest behind **Show all**; a `share` view draws that many parts and adds the rest together as one. 1 to 50. Left out, a bar view shows 12 and a share view 6. |
| `selects` | parameter name | The parameter a click on a mark sets to the mark's `by` value. Left out, the marks are not clickable. |

A column is named as the statement answers it, which for an expression is its `AS` alias. A name is at most 128 characters. The reader does not compare a column name with the statement: only the answer says which columns there are.

## A click that sets a parameter

`selects` turns a view into a filter for the rest of the report:

```yaml
parameters:
  source:
    label: Source

queries:
  by_source:
    sql: SELECT Source, COUNT(*) AS Staged FROM stg.Orders GROUP BY Source ORDER BY Staged DESC
    show: { type: bar, by: Source, values: Staged, selects: source }

  per_day:
    sql: |
      SELECT CAST(LoadedUtc AS date) AS Day, COUNT(*) AS Staged
      FROM stg.Orders
      WHERE (@source IS NULL OR Source = @source)
      GROUP BY CAST(LoadedUtc AS date)
      ORDER BY Day
    show: { type: line, by: Day, values: Staged }
```

A click on the bar of one source sets `source` to that bar's `Source`. `per_day` binds `@source`, so it runs again and now counts that source alone. `by_source` does not bind it, so its bars stay whole and the choice can be changed with another click. A second click on the same bar clears the parameter.

The value clicked is bound like any other value of the parameter: it is checked against the parameter's type, and against its `choices` when it has a fixed list ([parameters](rpt.md#parameters)).

## Rules the reader checks

| Problem | Message says |
| --- | --- |
| `selects` on a view with no `by` | `sets a parameter to the value clicked, so the view names the column that value is read from under 'by'` |
| `selects` names a parameter the report does not declare | Which parameters the report declares. |
| `show: matrix`, or a matrix mapping without `across` | `is a matrix, which needs 'across'`; name the rows under `by` and the columns under `across`. |
| Several columns under `values` of a `share` or a `matrix` | `a 'share' view shows one value` |
| `series` with several columns under `values` | A series splits one value: name one column, or leave `series` out to set the columns side by side. |
| A key the view's type does not take | `is a 'line' view, which takes no 'top'; 'top' goes with: bar, share.` |
| `none` beside other views | `a query is hidden or shown, not both` |
| `none` with another key | `is 'none', which shows nothing, so it takes no other key` |
| A mapping without `type` | The view types. |
| An unknown view type, width or format | The ones there are. One written in another case is answered with its spelling: `it is written 'integer', in lower case`. |
| A column named twice under `values` | Which one. |
| An empty list under `show` | Name how the answer is shown, or `none`. |
| More than 6 views | `holds 7 entries; it may hold at most 6` |
| Every query of the report has `show: none` | The report shows nothing; show at least one query. |

## columns

`columns` maps a column of the answer to how it is labelled and written. It applies to every view of the query, and only to the columns it names. A column is named once: names are compared without regard to case.

| Form | Example | Meaning |
| --- | --- | --- |
| A format | `Size: bytes` | The column is written in that format. |
| A mapping | `Staged: { label: Rows staged, format: integer }` | Any of `label`, `format`, `decimals` and `unit`. |

| Key | Type | Description |
| --- | --- | --- |
| `label` | string | What a person reads in place of the column's name. At most 100 characters. |
| `format` | string | One of the formats below. Left out, the column takes the format its type has. |
| `decimals` | integer | How many decimals are shown. 0 to 6. Goes with `number`, `percent`, `bytes` and `duration`, or with no `format`. Left out, the format's own. |
| `unit` | string | What follows each value: `rows`, `files`, `m`. At most 16 characters. |

| Format | A value is written as |
| --- | --- |
| `text` | As the query answers it. |
| `integer` | A whole number with digit grouping. |
| `number` | A number with digit grouping and decimals. |
| `percent` | A fraction as a percentage: 0.25 reads 25 %. |
| `bytes` | A count of bytes in the largest unit that fits: 1536 reads 1.5 KB. |
| `duration` | A number of seconds as a duration: 3725 reads 1 h 02 min. |
| `date` | A date without its time. |
| `datetime` | A date and time to the second. |
| `flag` | Yes or no. |
| `code` | Text in a fixed-width face: a key, a code, a file name. |

A format says how a value is written, and the statement still answers the value itself. `percent` takes a fraction, so compute `1.0 * Part / Whole` in the statement and leave the multiplication by 100 to the format. `bytes` takes a count of bytes and `duration` a number of seconds.

## Tiles

Each view is a tile. A tile's menu shows the answer as a table, downloads it as CSV, and shows how it was computed: the statement, the values bound, the rows and the time it took. An answer cut at `maxRows` says so.

## See also

- [Report (flowType: rpt)](rpt.md): the keys of the document, parameters, and what a statement may be.
- [Reports](../concepts/reports.md): how a query runs and what the page does.
- [Write a report](../guides/write-a-report.md)
