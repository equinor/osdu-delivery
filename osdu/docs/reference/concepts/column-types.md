---
id: delivery-concept-column-types
title: "Column types: the SQL type a source column should have for the property it fills"
type: concept
summary: "For each column a delivery flow's mapping reads: the properties it reaches, its declared and detected types, and the type to declare and why."
keywords:
  - column type
  - datatype
  - what datatype should a column be
  - declare a column type
  - transform.columns
  - bit for a number
  - a boolean is not a number
  - decimal(38,18)
  - column-types
  - delivery_column_types
  - value profile
  - infer_column_types
  - verdict
related:
  - delivery-concept-mapping-overview
  - delivery-concept-api
  - delivery-guide-mcp
  - concept-type-inference
  - concept-pre-ingestion-transform
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Rendering/ColumnTypeAdvice.cs
  - osdu/src/SqlFlow.Delivery/Rendering/NumberValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/DateValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.Conversion.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingOutline.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryColumnTypeEndpoints.cs
  - osdu/hosts/osdu-delivery-mcp/src/tools/documents.rs
---

# Column types

A source column reaches an OSDU property through the mapping, and the render accepts some SQL types for a property and
holds the record for others. `GET /api/v1/delivery/flows/{pipelineId}/column-types` (the MCP tool
`delivery_column_types`) says, for each column the flow's mapping reads, which type it should have and what its type does
now. It reads no row.

## What the answer holds

The mapping the flow pins is laid out against its template ([a mapping's overview](mapping-overview.md#the-outline)), so
each column of the record table and of the child datasets is set beside the properties it reaches: their paths, the type
the template gives each (`number`, `integer`, `boolean`, `date-time`, `date`, `reference`, `string`), and the steps the
value goes through. Beside them:

- `type`: the column's type in the table the flow reads, as the catalog holds it.
- `declaredType` and `detectedType`: the type the pre-ingestion flow upstream declares in `transform.columns`, and the one
  its last run's typed view gave the column. The flow is found by walking lineage up from the table the delivery reads, at
  most three flows, to the first one that carries transform columns; `declaredInFlow` names it. This is the flow to change.
- `profile`: the raw table that flow lands the column in, where its values can be profiled.

## The advice

`recommended` is the type to declare, from the property the column reaches:

| Property | Recommended | Why |
| --- | --- | --- |
| `number` | `decimal(38,18)` | Exact, with room for any depth or reading. |
| `integer` | `bigint` | A whole number. |
| `date-time`, `date` | `datetime2`, `date` | A date the render writes in UTC. |
| `boolean` | `bit` | True or false. |
| text (`string`, `reference`, any other format) | `nvarchar(max)` | Text, with a length the values fit where one is known. |

A column a modifier reads (trim, case, split, replace, equals, the date and number parsers, id, ref), or a lookup compares
to find a record, reaches its property as text whatever the property's type, so it is advised as text. A column that
reaches properties of different types is advised as text too, which each property's rule then reads.

`verdict` says what the column's type does now, by the render's own rules for the property:

- `fits`: every value renders as the property needs it.
- `parsed`: the render reads the value from text, and holds a record whose value cannot be read: a number from text with a
  point for decimals, text for a date written as it is (it must already be an RFC 3339 date, or a date modifier must read
  it), a whole number from a decimal with a fraction.
- `approximate`: the value renders, but not exactly: a floating point number's digits, or a code written from a number,
  which loses its leading zeros.
- `refused`: the render refuses every value of the type and holds every record that reads the column: a `bit` or a date
  for a number ("a boolean is not a number").
- `unknown`: the catalog holds no type for the column.

`reason` says the same in a sentence.

## The value counts

The type a pre flow's inference chooses can fit the values and still be wrong for the property: a column of only 0 and 1
is chosen as `bit`, and the property is a number. With `profile=true` the MCP tool queues the compute operation
`inferColumnTypes` once for each raw table ([type inference](../../../../sqlflow/docs/reference/concepts/type-inference.md#profiling-a-table-on-request)),
and sets each column's `valueProfile` beside it: the type inference would choose, the rows profiled and the `evidence`
(how many values convert to a boolean, a whole number, a decimal, a date and time, a date or a guid, and the longest
value). `valuesFit` is true when the count `profileCount` names covers every value profiled: the column's values convert
to what the property needs, so declaring `recommended` holds no record. The answer carries counts, lengths and types,
never a value.
