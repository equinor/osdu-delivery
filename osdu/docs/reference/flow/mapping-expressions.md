---
id: delivery-flow-mapping-expressions
title: "Mapping expressions ($expr, $when, $where): computing values and conditions from a row"
type: flow-reference
summary: "The expression language of $expr, $when and $where: columns, parameters, operators, functions, how no value, text, numbers and dates compare."
keywords:
  - expression
  - "$expr"
  - "$when"
  - "$where"
  - condition
  - iif
  - coalesce
  - nullif
  - operators
  - functions
  - "$param"
  - "$dataset"
  - backticks
  - no value
related:
  - delivery-flow-mapping-transformations
  - delivery-flow-mapping-values
  - delivery-flow-mapping
  - delivery-flow-mapping-modifiers
  - delivery-concept-preflight
  - delivery-cli-values
  - flow-ing
yamlPath: "record.<property>.$expr | $when | $where (documentType: mapping)"
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Expressions/MappingExpression.cs
  - osdu/src/SqlFlow.Delivery/Expressions/ExpressionParser.cs
  - osdu/src/SqlFlow.Delivery/Expressions/ExpressionNodes.cs
  - osdu/src/SqlFlow.Delivery/Expressions/ExpressionValues.cs
  - osdu/src/SqlFlow.Delivery/Expressions/ExpressionFunctions.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Rendering/EntryValues.cs
  - osdu/src/SqlFlow.Delivery/Rendering/SourceRow.cs
  - osdu/tests/SqlFlow.Delivery.Tests/ExpressionTests.cs
---

# Mapping expressions ($expr, $when, $where): computing values and conditions from a row

An expression computes a value, or decides a condition, from the row a [mapping](mapping.md) node reads. It is written in
three places and nowhere else:

| Where | What it gives | Example |
| --- | --- | --- |
| `$expr` | The property's value, which then passes through `$modifiers` and `$required` like a column's | `$expr: coalesce(log_name, log_source)` |
| `$when` | Whether the property is written for the row | `$when: depth_coding = "REGULAR"` |
| `$where` on a `$forEach` | Whether a child row becomes an item | `$where: curve_id != "DEPT" and not empty(curve_unit)` |

**Compute in the ingestion SQL; shape in the mapping.** An expression is for choosing, combining and cleaning the values
of one row on the way into the record: the first value that is there, a name built from two columns, a flag from a
status. A value that needs a join, more than a line, or arithmetic a reviewer has to think about belongs in the
[ingestion flow's](../../../../sqlflow/docs/reference/flow/ing.md) SQL as a column, where it is typed, tested and traced
by the table's own lineage. An expression is at most 2000 characters and nests at most 48 levels. It reads no clock, no
random source and nothing outside the row and the parameters, so a record renders the same every time its inputs do.

The language is small and fixed, with SQL's function names and behaviour where SQL has them.

## An example

```yaml
parameters:
  dataPartition: { required: true }
  aclOwner: { required: true }
  aclViewer: { required: true }
  legalTag: { required: true }
  sourceLabel:
    default: welldb
    description: The prefix written before each log's source.

record:
  # acl and legal as on the mapping page
  data:
    Name:
      $expr: coalesce(log_name, upper(trim(log_source)) & " run " & log_run)
    Source:
      $expr: $param.sourceLabel & ":" & log_source
    TopMeasuredDepth:
      $expr: nullif(index_min, -999.25) * 0.3048       # feet to metres; the placeholder gives no value
      $required: false
    SamplingInterval:
      $from: index_increment
      $when: depth_coding = "REGULAR" and index_increment > 0
    IsRegular:
      $expr: depth_coding = "REGULAR"                  # a condition used as a value: true or false
    LogRemark:
      $expr: iif(empty(log_comment), "No comment in the source", left(log_comment, 200))
    Curves:
      $forEach: curves
      $where: curve_id not in ["DEPT", "MD"] and not empty(curve_unit)
      $item:
        CurveID: { $from: curve_id }
        CurveDescription:
          $expr: coalesce(curve_description, curve_id & " of " & $dataset.log_name)
```

In a complete mapping document (with the `acl` and `legal` lists every mapping lays out), this validates with
`sqlflow validate`.

## What an expression reads

| Written | Means |
| --- | --- |
| `log_name` | A column of the row the node reads: the record table's row, or under a `$forEach` the child row. A bare name starts with a letter or `_` and holds letters, digits and `_`. |
| `` `curve-id` `` | A column whose name holds `-`, starts with a digit, or is a keyword or a function name. Backticks hold letters, digits, `_` and `-`. |
| `$dataset.log_name` | A column of the record table's row, from anywhere (`` $dataset.`log-name` `` too). |
| `$param.sourceLabel` | A parameter the mapping declares, as the flow gives it, else its default. Letters, digits and `_`. |
| `"FINAL"`, `'FINAL'` | Text. Escapes are `\\`, `\"`, `\'`, `\n`, `\t` and `\r`. |
| `12`, `12.5`, `1e3` | A number. Write `0.5`, not `.5`; a negative number is `-` before it. |
| `true`, `false` | True or false. |
| `null` | No value. |

Keywords (`and`, `or`, `not`, `in`, `true`, `false`, `null`) and function names are read ignoring case: `COALESCE(a, b)`
is `coalesce(a, b)`. Column names are matched ignoring case too. Every column an expression reads is checked against the
flow's ingestion tables before a run, and every parameter it reads must be declared by the mapping and have a value in
the flow ([preflight](../concepts/preflight.md)).

## Operators

From the loosest binding to the tightest; brackets say otherwise.

| Operator | Means |
| --- | --- |
| `or` | Either condition holds. The right side is read only when the left does not decide. |
| `and` | Both conditions hold. The right side is read only when the left does not decide. |
| `not` | The condition does not hold. |
| `=`, `!=`, `<`, `<=`, `>`, `>=` | Compare two values. One comparison at a time: `a > 1 and a < 5`, never `1 < a < 5`. |
| `x in [a, b]`, `x not in [a, b]` | Whether a value is one of a list of at least one value. |
| `&` | Join values as text: `log_source & " run " & log_run`. |
| `+`, `-` | Add, subtract. |
| `*`, `/` | Multiply, divide. |
| `-x` | Negate a number. |

`iif(condition, value, other)` chooses: `value` where the condition holds, `other` where it does not, and only the
branch taken is read. There is no `? :` and no `case`.

## How values behave

Most mistakes come from here.

- **No value.** A missing column, blank text and `null` are all no value. No value is the same only as no value, so
  `status != "FINAL"` holds for a row without a status and `status = "FINAL"` does not. An ordering comparison (`<`,
  `>`) with no value is false. Arithmetic with no value gives no value, as in SQL; `coalesce` says what to use instead.
  `&` reads no value as no text, and a join that gives blank text is no value.
- **Text** compares ignoring case and the spaces around it, as the rest of the mapping matches text: `status = "final"`
  holds for `" FINAL "`. Ordering text compares it the same way, character by character.
- **Numbers** compare as numbers: `depth > 100` holds for the text `"150"`, and `depth = 100` for `"100.0"`. A number
  compared for equality with text that is not a number is simply not equal; the same text ordered against a number, or
  used in arithmetic, cannot be worked with. Arithmetic is exact on decimals: `12.5 * 0.3048` is `3.81`.
- **Dates.** A value the ingestion table holds as a date or a date-time compares as an instant (a date at midnight UTC),
  against another date or against text in ISO 8601: `spud_date > "2020-01-01"`. Text that is not ISO 8601 cannot be
  ordered against a date. A date held as text in the table is text, and compares as text.
- **True and false** equal `true` and `false` or the text `"true"` and `"false"` (ignoring case). They cannot be ordered.
- **Conditions.** A condition is true, false, or no value, which counts as false. A value inside `and`, `or`, `not` or
  `iif` that is neither (a column holding `"Y"`) cannot be tested: compare it (`flag = "Y"`).
- **Joining.** `&` writes a number in its shortest form (`12.50` as `12.5`), true or false as `true` or `false`, and a
  date in RFC 3339.

## Functions

| Function | Gives |
| --- | --- |
| `coalesce(value, value, ...)` | The first value that is there: not missing and not blank. Two or more values. |
| `nullif(value, other)` | No value when `value` is the same as `other`, otherwise `value`: `nullif(depth, -999)` drops a placeholder, whether the row holds it as a number or as text. |
| `empty(value)` | True when the value is missing or blank. |
| `trim(text)` | The text without the spaces around it. |
| `upper(text)`, `lower(text)` | The text in upper case, in lower case. |
| `substring(text, start, length)` | Part of the text from the character at `start`, counting from 1, `length` characters long, or to the end when `length` is left out. A start past the end gives empty text. |
| `left(text, count)`, `right(text, count)` | The first or last `count` characters; `count` is a whole number, 0 or more. |
| `replace(text, find, with)` | The text with every occurrence of `find`, in any case, replaced by `with`. `find` must not be empty. |
| `length(text)` | How many characters the text holds. |
| `contains(text, part)`, `startsWith(text, part)`, `endsWith(text, part)` | Whether the text holds, starts with or ends with `part`, in any case. False for no value. |
| `number(value)` | The value as a number, text read with `.` before the decimals. The `number` modifier reads other forms ([mapping modifiers](mapping-modifiers.md)). |
| `text(value)` | The value as text: a number in its shortest form, true or false, a date in RFC 3339. |
| `round(number, digits)` | Rounded to `digits` decimals, 0 to 15 (0 when left out), halves away from zero as SQL rounds them. |
| `abs(number)` | The number without its sign. |

A text function given no value gives no value (`upper(name)` for a row without a name). Characters are counted as a
person reads them, so an accented letter or an emoji is one character to `length`, `left`, `right` and `substring`.

A name from another language is refused with the function this one calls it: `isnull`, `nvl` and `ifnull` are
`coalesce`; `len` and `char_length` are `length`; `substr` and `mid` are `substring`; `ucase` and `toUpperCase` are
`upper`; `lcase` and `toLowerCase` are `lower`; `string`, `toString`, `cast` and `convert` are `text`; `toNumber`,
`parse` and `val` are `number`; `isEmpty` and `isBlank` are `empty`. A near miss (`trimm`) is answered with the
function it most likely meant, `concat` with `&`, and `if`, `case`, `when` and `choose` with `iif`.

## Conditions: $when and $where

A `$when` or a `$where` must be a condition: a comparison, a test (`empty`, `contains`, `startsWith`, `endsWith`, `in`), or
a join of them with `and`, `or` and `not`. It must read a column, since one that reads none decides the same for every
row:

```text
record.data.Description: $when 'upper(b)' gives a value, and $when is a condition, which gives true or false: compare the value, such as $when: log_status = "FINAL", or test it, such as $when: not empty(log_status).
record.data.Description: $when '$param.dataPartition = "dev"' reads no column, so it decides the same way for every row; remove it, or compare a column, such as $when: log_status = "FINAL".
```

A condition in the form the language used to write is refused with the expression to write instead:

```text
record.data.Name: $when 'log_status is FINAL' is a condition as the mapping language no longer writes it; a condition is an expression now: $when: log_status = "FINAL"
```

(`x is not FINAL` becomes `x != "FINAL"`, `x is empty` becomes `empty(x)`, `x is not empty` becomes `not empty(x)`.)

A `$where` reads the child row by bare names and the record table's row with `$dataset.<column>`. A `$when` on a
`$forEach` reads the record table's row, since it decides for the whole array. An `$expr` may be a condition too: it then
gives true or false, which suits a boolean property (`IsRegular: { $expr: depth_coding = "REGULAR" }`).

## Writing an expression in YAML

An expression is a YAML scalar first, and YAML reads some characters before the expression language sees them.

| The expression | What YAML does | Write it |
| --- | --- | --- |
| starts with a quote: `"Run " & log_run` | reads `"Run "` as the whole value and refuses the rest (`While scanning an anchor or alias, found value containing disallowed: []{},`) | `$expr: '"Run " & log_run'` |
| starts with a backtick: `` `log-name` `` | refuses the character (`found character that cannot start any token`) | ``$expr: '`log-name`'`` |
| holds `: ` | reads a mapping (`While scanning a plain scalar value, found invalid mapping.`) | quote it |
| holds ` #` | cuts the rest off as a comment | quote it |
| holds `,` inside `{ ... }` | splits the flow-style map at the comma | write the node in block style, or quote the expression |

Single quotes keep the double quotes inside as they are, which is why they are the usual choice. Block style (one key
per line, as in the example above) avoids the comma trap entirely:

```yaml
Description:
  $expr: coalesce(log_description, log_name)
```

## Errors when the mapping is read

An expression the language cannot read is refused when the mapping is read, naming the node, the expression, where in it
and what to write instead. Each message starts with the file:

| Written | Message |
| --- | --- |
| `a == b` | `record.data.Description: $expr 'a == b': '==' at character 3: an expression compares with a single '=', such as status = "FINAL".` |
| `trimm(a)` | `record.data.Description: $expr 'trimm(a)': 'trimm' (at character 1) is not a function of the expression language. Did you mean trim(text)?.` |
| `len(a)` | `... 'len' (at character 1) is not a function of the expression language. Did you mean length(text)?.` |
| `dataset.a` | `... 'dataset.a' (at character 1) reads a column the way the mapping language no longer writes it: a column of the dataset's own row is $dataset.a, and a column of the row the node reads is a alone.` |
| `a + "x"` | `... '"x"' gives text, and '+' computes with numbers; join text with '&', such as a & "-" & b.` |
| `a & $param.region` | `record.data.Description reads $param.region in 'a & $param.region', but the mapping declares no parameter 'region'.` |
| `upper("x")` as `$expr` | `record.data.Description: $expr 'upper("x")' reads no column, so every record gets the same value; write it as a literal, whose text reads a parameter as {$param.<name>}.` |
| `.5 * depth` | `... '.' at character 1 is not expected where a value belongs.` |

Other refusals, quoted from the parser: `<>` (`writes 'is not equal' as '!='`), `&&` (`joins conditions with the word
and`), `||` (`joins conditions with the word or, and joins text with '&'`), `??` (`takes the first value that is there
with coalesce(a, b)`), `? :` (`chooses with iif(condition, value, other)`), `!` (`negates a condition with the word not`),
`is` (`'is' ... is not an operator of the expression language: compare with = or != ..., and test for a value with
empty(status) or not empty(status)`), `a = 1 = 2` (`chains comparisons`), `a in []` (`'in' looks for a value among at
least one`), `coalesce(a)` (`it takes at least 2 values: coalesce(value, value, ...)`), `iif(a, b)` (`it takes 3:
iif(condition, value, other)`), `upper(a) and b = 1` (`the left side of 'and' is 'upper(a)', which gives text, not true or
false`), `upper(a = 1)` (`a value given to upper is 'a = 1', a condition; conditions are joined with and, or and not, and
choose between values with iif(condition, value, other), but are not compared or combined as values`), `and` as a
column (`a column named and is written in backticks`), `1a` (`a column whose name starts with a digit
is written in backticks`), and an expression over the limits (`is at most 2000`, `nests more than 48 levels deep`).

## Errors at render

A row holding a value the expression cannot work with holds the record, naming the variable, the expression, the part
and the value. That is a row the mapping did not expect, and the record goes nowhere until the row or the mapping says
what it is:

```text
osdu.data.Depth: nullif(depth, -999) * 0.3048: nullif(depth, -999) is 'deep', which is not a number, and '*' computes with numbers
osdu.data.Count: flag in ["REGULAR", "IRREGULAR"] and depth > 10: depth is 'deep', which is not a number, and '>' compares it with the number 10
```

A `$when` or a `$where` the row cannot test holds the record the same way: whether the property, or the child row,
belongs in the record is then unknown. Division by zero (`depth / 0 divides by zero`), a result too large to hold, a
`substring` start below 1, a negative or fractional count, and `round` beyond 15 decimals hold the record too. An
`$expr` that gives no value is an empty value, and `$required` decides
([mapping values](mapping-values.md#required-what-an-empty-value-does)).

To see what an expression makes of every row of a scope before anything is sent, check the mapping's values
([values](../cli/values.md)), or preview one record ([preview](../cli/preview.md)).

## Related

- [Mapping transformations](mapping-transformations.md): every function and modifier on one page, with results.
- [Mapping values](mapping-values.md): `$expr`, `$when` and `$where` among the other words of a node.
- [Mapping modifiers](mapping-modifiers.md): `trim`, `replace`, `date`, `number` and the id builders, applied after `$expr`.
- [Mapping](mapping.md): the document as a whole.
- SQLFlow's [ingestion flow](../../../../sqlflow/docs/reference/flow/ing.md), where heavier computation belongs.
