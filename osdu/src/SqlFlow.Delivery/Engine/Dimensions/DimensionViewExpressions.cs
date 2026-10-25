using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlFlow.Core;
using SqlFlow.SqlServer.Schema;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// The kind of value a view's expression gives (osdu/docs/reference/flow/dimension-views.md): what it may be compared
/// with, combined with and converted to. Two kinds meet only where SQL Server would not convert one to the other
/// implicitly, since such a conversion fails the whole read at the first value it cannot convert.
/// </summary>
public enum ViewValue
{
    /// <summary>A NULL literal, which meets every kind.</summary>
    Null,

    /// <summary>Text: every column of a dimension's table but its numbers.</summary>
    Text,

    /// <summary>A number of any numeric type, and a bit.</summary>
    Number,

    /// <summary>A date, a <c>datetime2</c> or a <c>datetimeoffset</c>.</summary>
    Date,

    /// <summary>A time of day.</summary>
    Time,

    /// <summary>A <c>uniqueidentifier</c>.</summary>
    UniqueId,
}

/// <summary>
/// A column an expression may name: the alias the document reads its table by (none for the from dimension's), the
/// table's alias in the SQL, the column, the kind of value it holds and its type.
/// </summary>
public sealed record ViewColumnSource(string Alias, string SqlAlias, string Column, ViewValue Value, string SqlType)
{
    /// <summary>The column as the document names it: bare for the from dimension's, <c>alias.column</c> for a join's.</summary>
    public string Named => Alias.Length == 0 ? Column : $"{Alias}.{Column}";

    /// <summary>The column as the SQL names it: <c>[b].[TopDepth]</c>.</summary>
    public string Sql => $"[{SqlAlias}].{DimensionViewExpressions.Quoted(Column)}";
}

/// <summary>An expression compiled: the SQL the view computes it by, the kind of value it gives, and the column it is, when it is one alone.</summary>
/// <param name="Sql">The SQL the view computes the expression by.</param>
/// <param name="Value">The kind of value the expression gives.</param>
/// <param name="Column">The column the expression is, when it is one alone; null otherwise.</param>
/// <param name="Orders">
/// The orders the expression's ordered aggregates read the rows of a group in (<c>STRING_AGG ... WITHIN GROUP</c>), each
/// once: SQL Server reads a group in one order, so a grouped view's ordered aggregates all name the same one.
/// </param>
public sealed record CompiledViewExpression(string Sql, ViewValue Value, ViewColumnSource? Column, IReadOnlyList<string>? Orders = null);

/// <summary>An expression or a data type a view cannot use, with the reason as a sentence the document's error ends with.</summary>
public sealed class ViewExpressionException : Exception
{
    public ViewExpressionException()
    {
    }

    public ViewExpressionException(string message)
        : base(message)
    {
    }

    public ViewExpressionException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The tables a view's expressions read: its <c>from</c> dimension's, whose columns an expression names bare, and each
/// join's, whose columns it names by the join's alias (<c>Unit.Name</c>). Names compare ignoring case, as SQL Server's
/// do on a database of the usual collation, and are always written as the document declares them.
/// </summary>
public sealed class ViewScope
{
    private readonly Dictionary<string, (string Dimension, Dictionary<string, ViewColumnSource> Columns)> _tables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The alias the <c>from</c> dimension's columns are named by: none.</summary>
    public const string FromAlias = "";

    /// <summary>Adds a table: the <c>from</c> dimension's under <see cref="FromAlias"/>, or a join's under its alias.</summary>
    public void Add(string alias, string dimension, IEnumerable<ViewColumnSource> columns)
    {
        ArgumentNullException.ThrowIfNull(alias);
        ArgumentNullException.ThrowIfNull(columns);
        _tables[alias] = (dimension, columns.ToDictionary(c => c.Column, c => c, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Whether a table is read under <paramref name="alias"/>.</summary>
    public bool Has(string alias) => _tables.ContainsKey(alias);

    /// <summary>The column <paramref name="column"/> of the table read under <paramref name="alias"/>.</summary>
    /// <exception cref="ViewExpressionException">No table is read under that alias, or it has no such column.</exception>
    public ViewColumnSource Resolve(string alias, string column)
    {
        if (!_tables.TryGetValue(alias, out var table))
        {
            var joins = _tables.Keys.Where(k => k.Length > 0).ToList();
            throw new ViewExpressionException(
                $"names {alias}.{column}, and {alias} is no join of the view before it{(joins.Count == 0 ? "; the view joins nothing, so a column is named bare" : $"; its joins are {string.Join(", ", joins)}")}");
        }

        if (table.Columns.TryGetValue(column, out var found))
        {
            return found;
        }

        var where = alias.Length == 0 ? $"the table of {table.Dimension}, the view's from dimension," : $"the table of {table.Dimension}, joined as {alias},";
        throw new ViewExpressionException(
            $"names {(alias.Length == 0 ? column : $"{alias}.{column}")}, and {where} has no column {column}; it has {string.Join(", ", table.Columns.Keys)}");
    }
}

/// <summary>
/// A view's T-SQL scalar expression (osdu/docs/reference/flow/dimension-views.md), read by SQL Server's own parser and
/// written into the view by the module from what the parser read, never from the text as written. Only the listed
/// operators and functions are used, every column is one of the tables the view reads, every conversion is the module's
/// (<see cref="DimensionConversions"/>), an implicit conversion is refused, and what could fail a read is written so it
/// cannot: a division by zero, a root or a logarithm outside its domain and a negative length are null. A view that groups
/// its rows reads the columns it groups by and aggregates of the rest (<see cref="Aggregates"/>); every other view reads
/// one row at a time and aggregates nothing.
/// </summary>
public static class DimensionViewExpressions
{
    /// <summary>The longest expression a column takes.</summary>
    public const int MaxLength = 4000;

    /// <summary>How deep an expression nests.</summary>
    public const int MaxDepth = 64;

    /// <summary>The longest SQL one expression is written as, its guards and conversions included.</summary>
    public const int MaxSqlLength = 65_536;

    /// <summary>The functions an expression may call, as the reference lists them.</summary>
    public static IReadOnlyList<string> Functions { get; } =
    [
        "COALESCE", "NULLIF", "ISNULL", "IIF", "TRY_CAST", "TRY_CONVERT", "CAST", "CONVERT",
        "LEFT", "RIGHT", "SUBSTRING", "LEN", "UPPER", "LOWER", "TRIM", "LTRIM", "RTRIM", "REPLACE", "CHARINDEX", "CONCAT", "CONCAT_WS",
        "ABS", "ROUND", "FLOOR", "CEILING", "POWER", "SQRT", "EXP", "LOG", "LOG10", "SIGN",
        "DATEADD", "DATEDIFF", "DATEPART", "YEAR", "MONTH", "DAY", "EOMONTH", "STRING_ESCAPE",
    ];

    /// <summary>The aggregates a grouped view's expression may call, as the reference lists them.</summary>
    public static IReadOnlyList<string> Aggregates { get; } = ["COUNT", "COUNT_BIG", "MAX", "MIN", "SUM", "AVG", "STRING_AGG"];

    /// <summary>
    /// The collation text is ordered by where an aggregate orders it: its UTF-8 bytes, which is the order of its code
    /// points, as the dimension engine's <c>many: max</c> and Spark's <c>max</c> order text. SQL Server's binary
    /// collations order <c>nvarchar</c> by UTF-16 code unit, which puts a character past U+FFFF before one from U+E000
    /// to U+FFFF; the text is ordered as <c>varchar</c> of this collation, and read back as <c>nvarchar</c> unchanged.
    /// </summary>
    public const string CodePointCollation = "Latin1_General_100_BIN2_UTF8";

    /// <summary><paramref name="sql"/>, a text, as what orders it by code point: its UTF-8 bytes.</summary>
    public static string ByCodePoint(string sql) => $"CAST(({sql}) COLLATE {CodePointCollation} AS varchar(max))";

    /// <summary>The parts of a date or a time <c>DATEADD</c>, <c>DATEDIFF</c> and <c>DATEPART</c> take, with their abbreviations.</summary>
    private static readonly HashSet<string> DateParts = new(
        [
            "year", "yy", "yyyy", "quarter", "qq", "q", "month", "mm", "m", "dayofyear", "dy", "y", "day", "dd", "d", "week", "wk", "ww",
            "weekday", "dw", "w", "hour", "hh", "minute", "mi", "n", "second", "ss", "s", "millisecond", "ms", "microsecond", "mcs",
            "nanosecond", "ns",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>A number literal as the parser keeps its text.</summary>
    private static readonly Regex NumberText = new(@"^[0-9]*\.?[0-9]*([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Compiles <paramref name="text"/> over the tables <paramref name="scope"/> reads: for a view that groups its rows by
    /// <paramref name="groupedBy"/>, an expression of those columns and of aggregates of any; else of one row.
    /// </summary>
    /// <exception cref="ViewExpressionException">The text is not an expression a view may use; the message says why.</exception>
    public static CompiledViewExpression Compile(string text, ViewScope scope, IReadOnlyList<ViewColumnSource>? groupedBy = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ViewExpressionException("is empty; give the expression the column is computed by, such as a column's name");
        }

        if (text.Length > MaxLength)
        {
            throw new ViewExpressionException(string.Create(CultureInfo.InvariantCulture, $"is {text.Length} characters; an expression is at most {MaxLength}"));
        }

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        ScalarExpression? parsed;
        IList<ParseError> errors;
        using (var reader = new StringReader(text))
        {
            parsed = parser.ParseExpression(reader, out errors);
        }

        if (errors.Count > 0 || parsed is null)
        {
            using var again = new StringReader(text);
            var condition = parser.ParseBooleanExpression(again, out var conditionErrors);
            if (conditionErrors.Count == 0 && condition is not null)
            {
                throw new ViewExpressionException(
                    "is a condition, and a column holds a value; write IIF(<condition>, 1, 0), or CASE WHEN <condition> THEN <value> ELSE <value> END");
            }

            var first = errors.FirstOrDefault();
            throw new ViewExpressionException(first is null
                ? "does not parse as a T-SQL expression"
                : string.Create(CultureInfo.InvariantCulture, $"does not parse as a T-SQL expression: {first.Message} (column {first.Column})"));
        }

        var compiler = new Compiler(scope, groupedBy);
        var compiled = compiler.Scalar(parsed, 0);
        var orders = compiler.Orders.Distinct(StringComparer.Ordinal).ToList();
        if (orders.Count > 1)
        {
            throw new ViewExpressionException(
                $"orders the rows of a group {orders.Count} ways ({string.Join("; ", orders)}), and SQL Server reads a group in one order: order every STRING_AGG of a grouped view alike, or in none");
        }

        return compiled.Sql.Length > MaxSqlLength
            ? throw new ViewExpressionException(string.Create(CultureInfo.InvariantCulture,
                $"would be written as {compiled.Sql.Length} characters of SQL with its guards and conversions, and a column is at most {MaxSqlLength}; split it over several columns"))
            : compiled with { Orders = orders };
    }

    /// <summary>
    /// Compiles <paramref name="text"/>, the condition a view keeps its rows by (its <c>where</c>), over the tables
    /// <paramref name="scope"/> reads, with the operators, functions and rules a column's expression has.
    /// </summary>
    /// <returns>The SQL the view's <c>WHERE</c> clause is written with.</returns>
    /// <exception cref="ViewExpressionException">The text is not a condition a view may use; the message says why.</exception>
    public static string CompileCondition(string text, ViewScope scope)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ViewExpressionException("is empty; give the condition a row is kept by, such as element IS NOT NULL");
        }

        if (text.Length > MaxLength)
        {
            throw new ViewExpressionException(string.Create(CultureInfo.InvariantCulture, $"is {text.Length} characters; a condition is at most {MaxLength}"));
        }

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        BooleanExpression? parsed;
        IList<ParseError> errors;
        using (var reader = new StringReader(text))
        {
            parsed = parser.ParseBooleanExpression(reader, out errors);
        }

        if (errors.Count > 0 || parsed is null)
        {
            using var again = new StringReader(text);
            var value = parser.ParseExpression(again, out var valueErrors);
            if (valueErrors.Count == 0 && value is not null)
            {
                throw new ViewExpressionException("is a value, and a row is kept by a condition; compare it (element IS NOT NULL, Source = 'SMDA')");
            }

            var first = errors.FirstOrDefault();
            throw new ViewExpressionException(first is null
                ? "does not parse as a T-SQL condition"
                : string.Create(CultureInfo.InvariantCulture, $"does not parse as a T-SQL condition: {first.Message} (column {first.Column})"));
        }

        var sql = new Compiler(scope).Boolean(parsed, 0);
        return sql.Length > MaxSqlLength
            ? throw new ViewExpressionException(string.Create(CultureInfo.InvariantCulture,
                $"would be written as {sql.Length} characters of SQL with its guards and conversions, and a condition is at most {MaxSqlLength}"))
            : sql;
    }

    /// <summary>An identifier in brackets, a closing bracket inside it doubled.</summary>
    public static string Quoted(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    /// <summary>A kind of value as a sentence names it.</summary>
    public static string Word(ViewValue value) => value switch
    {
        ViewValue.Text => "text",
        ViewValue.Number => "a number",
        ViewValue.Date => "a date",
        ViewValue.Time => "a time of day",
        ViewValue.UniqueId => "a GUID",
        _ => "NULL",
    };

    /// <summary>A kind of value as a column's type is shown when the expression gives no data type of its own.</summary>
    public static string TypeWord(ViewValue value) => value switch
    {
        ViewValue.Text => "text",
        ViewValue.Number => "number",
        ViewValue.Date => "date",
        ViewValue.Time => "time",
        ViewValue.UniqueId => "guid",
        _ => "null",
    };

    private sealed class Compiler(ViewScope scope, IReadOnlyList<ViewColumnSource>? groupedBy = null)
    {
        /// <summary>How many aggregates the expression being compiled is inside: a column of a grouped view outside every one must be a column it groups by.</summary>
        private int _inAggregate;

        /// <summary>The order each ordered aggregate of the expression reads its group in, as the SQL writes it, ascending written as no direction.</summary>
        public List<string> Orders { get; } = [];

        public CompiledViewExpression Scalar(ScalarExpression node, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ViewExpressionException(string.Create(CultureInfo.InvariantCulture, $"nests deeper than {MaxDepth}; split it over several columns"));
            }

            // COLLATE is kept on the value it follows, not as a node of its own: a view compares text by the database's rules,
            // and a join by its own exact one.
            if (node is PrimaryExpression { Collation: not null })
            {
                throw new ViewExpressionException("uses COLLATE, which a view's expression may not; text compares by the database's collation, and a join exactly");
            }

            var next = depth + 1;
            switch (node)
            {
                case ColumnReferenceExpression column:
                    return Column(column);
                case IntegerLiteral or NumericLiteral or RealLiteral:
                    var number = ((Literal)node).Value;
                    return NumberText.IsMatch(number) && number.Any(char.IsAsciiDigit)
                        ? new CompiledViewExpression(number, ViewValue.Number, null)
                        : throw new ViewExpressionException($"holds the number '{number}', which is not one a view writes");
                case StringLiteral text:
                    return new CompiledViewExpression("N'" + text.Value.Replace("'", "''", StringComparison.Ordinal) + "'", ViewValue.Text, null);
                case NullLiteral:
                    return new CompiledViewExpression("NULL", ViewValue.Null, null);
                case ParenthesisExpression parenthesis:
                    var inner = Scalar(parenthesis.Expression, next);
                    return inner with { Sql = $"({inner.Sql})", Column = null };
                case UnaryExpression unary:
                    return Unary(unary, next);
                case BinaryExpression binary:
                    return Binary(binary, next);
                case CastCall cast:
                    return Conversion(cast.Parameter, cast.DataType, null, next);
                case TryCastCall tryCast:
                    return Conversion(tryCast.Parameter, tryCast.DataType, null, next);
                case ConvertCall convert:
                    return Conversion(convert.Parameter, convert.DataType, convert.Style, next);
                case TryConvertCall tryConvert:
                    return Conversion(tryConvert.Parameter, tryConvert.DataType, tryConvert.Style, next);
                case CoalesceExpression coalesce:
                    var values = coalesce.Expressions.Select(e => Scalar(e, next)).ToList();
                    return new CompiledViewExpression($"COALESCE({string.Join(", ", values.Select(v => v.Sql))})", Unify(values, "COALESCE's values"), null);
                case NullIfExpression nullIf:
                    var first = Scalar(nullIf.FirstExpression, next);
                    var second = Scalar(nullIf.SecondExpression, next);
                    return new CompiledViewExpression($"NULLIF({first.Sql}, {second.Sql})", Unify([first, second], "NULLIF's values"), null);
                case IIfCall iif:
                    var then = Scalar(iif.ThenExpression, next);
                    var otherwise = Scalar(iif.ElseExpression, next);
                    return new CompiledViewExpression(
                        $"IIF({Boolean(iif.Predicate, next)}, {then.Sql}, {otherwise.Sql})", Unify([then, otherwise], "IIF's two values"), null);
                case SearchedCaseExpression searched:
                    return Searched(searched, next);
                case SimpleCaseExpression simple:
                    return Simple(simple, next);
                case LeftFunctionCall left:
                    return LeftOrRight("LEFT", left.Parameters, next);
                case RightFunctionCall right:
                    return LeftOrRight("RIGHT", right.Parameters, next);
                case FunctionCall call:
                    return Function(call, next);
                default:
                    throw new ViewExpressionException($"uses {Describe(node)}, which a view's expression may not: it reads the dimensions it joins and nothing else");
            }
        }

        private CompiledViewExpression Column(ColumnReferenceExpression column)
        {
            var parts = column.MultiPartIdentifier?.Identifiers;
            if (column.ColumnType != ColumnType.Regular || parts is null || parts.Count is < 1 or > 2)
            {
                throw new ViewExpressionException(
                    "names a column otherwise than a view does: a column of the from dimension is named bare (Mnemonic), one of a join by the join's alias (Unit.Name)");
            }

            var source = parts.Count == 1 ? scope.Resolve(ViewScope.FromAlias, parts[0].Value) : scope.Resolve(parts[0].Value, parts[1].Value);
            if (groupedBy is not null && _inAggregate == 0 && !groupedBy.Any(g => string.Equals(g.Sql, source.Sql, StringComparison.Ordinal)))
            {
                throw new ViewExpressionException(
                    $"names {source.Named} outside an aggregate, and the view groups its rows by {string.Join(", ", groupedBy.Select(g => g.Named))}: a grouped view's column reads a column it groups by, or an aggregate of the rows of its group, such as MAX({source.Named}), COUNT({source.Named}) or STRING_AGG({source.Named}, ', ')");
            }

            return new CompiledViewExpression(source.Sql, source.Value, source);
        }

        private CompiledViewExpression Unary(UnaryExpression unary, int depth)
        {
            if (unary.UnaryExpressionType is not (UnaryExpressionType.Positive or UnaryExpressionType.Negative))
            {
                throw new ViewExpressionException("uses a bitwise operator, which a view's expression may not");
            }

            var operand = Scalar(unary.Expression, depth);
            Require(operand, ViewValue.Number, unary.UnaryExpressionType == UnaryExpressionType.Negative ? "a minus sign" : "a plus sign");
            return new CompiledViewExpression($"({(unary.UnaryExpressionType == UnaryExpressionType.Negative ? "-" : "+")}{operand.Sql})", ViewValue.Number, null);
        }

        private CompiledViewExpression Binary(BinaryExpression binary, int depth)
        {
            var symbol = binary.BinaryExpressionType switch
            {
                BinaryExpressionType.Add => "+",
                BinaryExpressionType.Subtract => "-",
                BinaryExpressionType.Multiply => "*",
                BinaryExpressionType.Divide => "/",
                BinaryExpressionType.Modulo => "%",
                _ => throw new ViewExpressionException("uses a bitwise operator, which a view's expression may not"),
            };

            var left = Scalar(binary.FirstExpression, depth);
            var right = Scalar(binary.SecondExpression, depth);

            // + joins two texts; every other use of an operator is arithmetic on numbers. A date moves by DATEADD.
            if (symbol == "+" && (left.Value == ViewValue.Text || right.Value == ViewValue.Text))
            {
                Require(left, ViewValue.Text, "+ joining texts");
                Require(right, ViewValue.Text, "+ joining texts");
                return new CompiledViewExpression($"({left.Sql} + {right.Sql})", ViewValue.Text, null);
            }

            Require(left, ViewValue.Number, $"the operator {symbol}");
            Require(right, ViewValue.Number, $"the operator {symbol}");
            return symbol is "/" or "%"
                ? new CompiledViewExpression($"({left.Sql} {symbol} NULLIF({right.Sql}, 0))", ViewValue.Number, null)
                : new CompiledViewExpression($"({left.Sql} {symbol} {right.Sql})", ViewValue.Number, null);
        }

        private CompiledViewExpression Conversion(ScalarExpression parameter, DataTypeReference type, ScalarExpression? style, int depth)
        {
            if (style is not null)
            {
                throw new ViewExpressionException(
                    "converts with a style, which the module's conversions take none of: a date is read as ISO 8601 and a number as SQL Server writes one, whatever the reading session's settings");
            }

            var value = Scalar(parameter, depth);
            var target = DimensionConversions.Parse(TypeText(type));
            return new CompiledViewExpression(DimensionConversions.Convert(value.Sql, value.Value, target), DimensionConversions.ValueOf(target), null);
        }

        private CompiledViewExpression Searched(SearchedCaseExpression searched, int depth)
        {
            var results = new List<CompiledViewExpression>();
            var whens = new List<string>();
            foreach (var when in searched.WhenClauses)
            {
                var result = Scalar(when.ThenExpression, depth);
                results.Add(result);
                whens.Add($"WHEN {Boolean(when.WhenExpression, depth)} THEN {result.Sql}");
            }

            var otherwise = searched.ElseExpression is null ? null : Scalar(searched.ElseExpression, depth);
            if (otherwise is not null)
            {
                results.Add(otherwise);
            }

            return new CompiledViewExpression(
                $"CASE {string.Join(" ", whens)}{(otherwise is null ? string.Empty : $" ELSE {otherwise.Sql}")} END", Unify(results, "CASE's values"), null);
        }

        private CompiledViewExpression Simple(SimpleCaseExpression simple, int depth)
        {
            var input = Scalar(simple.InputExpression, depth);
            var compared = new List<CompiledViewExpression> { input };
            var results = new List<CompiledViewExpression>();
            var whens = new List<string>();
            foreach (var when in simple.WhenClauses)
            {
                var test = Scalar(when.WhenExpression, depth);
                compared.Add(test);
                var result = Scalar(when.ThenExpression, depth);
                results.Add(result);
                whens.Add($"WHEN {test.Sql} THEN {result.Sql}");
            }

            _ = Unify(compared, "CASE's value and what it is compared with");
            var otherwise = simple.ElseExpression is null ? null : Scalar(simple.ElseExpression, depth);
            if (otherwise is not null)
            {
                results.Add(otherwise);
            }

            return new CompiledViewExpression(
                $"CASE {input.Sql} {string.Join(" ", whens)}{(otherwise is null ? string.Empty : $" ELSE {otherwise.Sql}")} END", Unify(results, "CASE's values"), null);
        }

        private CompiledViewExpression LeftOrRight(string name, IList<ScalarExpression> parameters, int depth)
        {
            var args = Arguments(name, parameters, 2, 2, depth);
            Require(args[0], ViewValue.Text, name);
            Require(args[1], ViewValue.Number, name);
            return new CompiledViewExpression($"{name}({args[0].Sql}, {NotNegative(args[1].Sql)})", ViewValue.Text, null);
        }

        private CompiledViewExpression Function(FunctionCall call, int depth)
        {
            var name = call.FunctionName?.Value?.ToUpperInvariant() ?? string.Empty;
            if (call.CallTarget is not null)
            {
                throw new ViewExpressionException($"calls {name} on an object or a schema, which a view's expression may not; it calls the listed functions alone");
            }

            if (Aggregates.Contains(name, StringComparer.Ordinal))
            {
                return Aggregate(call, name, depth);
            }

            if (call.OverClause is not null || call.WithinGroupClause is not null)
            {
                throw new ViewExpressionException($"calls {name} over a window or a group, which a view's expression may not: a row of the view is computed from its own row alone");
            }

            if (call.UniqueRowFilter != UniqueRowFilter.NotSpecified || call.TrimOptions is not null || call.JsonParameters is { Count: > 0 })
            {
                throw new ViewExpressionException($"calls {name} with options a view's expression does not take; give it its arguments alone");
            }

            IList<ScalarExpression> parameters = call.Parameters ?? [];
            switch (name)
            {
                case "ISNULL":
                {
                    var args = Arguments(name, parameters, 2, 2, depth);
                    return new CompiledViewExpression($"ISNULL({args[0].Sql}, {args[1].Sql})", Unify(args, "ISNULL's values"), null);
                }

                case "LEN":
                {
                    var args = Arguments(name, parameters, 1, 1, depth);
                    Require(args[0], ViewValue.Text, name);
                    return new CompiledViewExpression($"LEN({args[0].Sql})", ViewValue.Number, null);
                }

                case "UPPER" or "LOWER" or "TRIM" or "LTRIM" or "RTRIM":
                {
                    var args = Arguments(name, parameters, 1, 1, depth);
                    Require(args[0], ViewValue.Text, name);
                    return new CompiledViewExpression($"{name}({args[0].Sql})", ViewValue.Text, null);
                }

                case "REPLACE":
                {
                    var args = Arguments(name, parameters, 3, 3, depth);
                    args.ForEach(a => Require(a, ViewValue.Text, name));
                    return new CompiledViewExpression($"REPLACE({args[0].Sql}, {args[1].Sql}, {args[2].Sql})", ViewValue.Text, null);
                }

                case "SUBSTRING":
                {
                    var args = Arguments(name, parameters, 3, 3, depth);
                    Require(args[0], ViewValue.Text, name);
                    Require(args[1], ViewValue.Number, name);
                    Require(args[2], ViewValue.Number, name);
                    return new CompiledViewExpression($"SUBSTRING({args[0].Sql}, {args[1].Sql}, {NotNegative(args[2].Sql)})", ViewValue.Text, null);
                }

                case "CHARINDEX":
                {
                    var args = Arguments(name, parameters, 2, 3, depth);
                    Require(args[0], ViewValue.Text, name);
                    Require(args[1], ViewValue.Text, name);
                    if (args.Count == 3)
                    {
                        Require(args[2], ViewValue.Number, name);
                    }

                    return new CompiledViewExpression($"CHARINDEX({string.Join(", ", args.Select(a => a.Sql))})", ViewValue.Number, null);
                }

                case "CONCAT" or "CONCAT_WS":
                {
                    var args = Arguments(name, parameters, name == "CONCAT" ? 2 : 3, 254, depth);

                    // CONCAT turns a number into text as CAST does, which keeps six digits of a float: the module's own conversion keeps them all.
                    args.ForEach(a => Require(a, ViewValue.Text, $"{name}, which joins texts (convert a value first: CAST(TopDepth AS nvarchar(40)))"));
                    return new CompiledViewExpression($"{name}({string.Join(", ", args.Select(a => a.Sql))})", ViewValue.Text, null);
                }

                case "ABS" or "FLOOR" or "CEILING" or "SIGN" or "EXP":
                {
                    var args = Arguments(name, parameters, 1, 1, depth);
                    Require(args[0], ViewValue.Number, name);
                    return new CompiledViewExpression($"{name}({args[0].Sql})", ViewValue.Number, null);
                }

                case "SQRT":
                {
                    var args = Arguments(name, parameters, 1, 1, depth);
                    Require(args[0], ViewValue.Number, name);
                    return new CompiledViewExpression($"SQRT(CASE WHEN {args[0].Sql} >= 0 THEN {args[0].Sql} END)", ViewValue.Number, null);
                }

                case "LOG" or "LOG10":
                {
                    var args = Arguments(name, parameters, 1, name == "LOG" ? 2 : 1, depth);
                    args.ForEach(a => Require(a, ViewValue.Number, name));
                    var value = $"CASE WHEN {args[0].Sql} > 0 THEN {args[0].Sql} END";
                    return new CompiledViewExpression(
                        args.Count == 2
                            ? $"{name}({value}, CASE WHEN {args[1].Sql} > 0 AND {args[1].Sql} <> 1 THEN {args[1].Sql} END)"
                            : $"{name}({value})",
                        ViewValue.Number,
                        null);
                }

                case "POWER":
                {
                    var args = Arguments(name, parameters, 2, 2, depth);
                    args.ForEach(a => Require(a, ViewValue.Number, name));
                    var (x, y) = (args[0].Sql, args[1].Sql);
                    return new CompiledViewExpression(
                        $"POWER(CASE WHEN ({x} = 0 AND {y} < 0) OR ({x} < 0 AND {y} <> FLOOR({y})) THEN NULL ELSE {x} END, {y})", ViewValue.Number, null);
                }

                case "ROUND":
                {
                    var args = Arguments(name, parameters, 2, 3, depth);
                    args.ForEach(a => Require(a, ViewValue.Number, name));
                    return new CompiledViewExpression($"ROUND({string.Join(", ", args.Select(a => a.Sql))})", ViewValue.Number, null);
                }

                case "DATEADD":
                {
                    if (parameters.Count != 3)
                    {
                        throw Count(name, 3, 3, parameters.Count);
                    }

                    var part = DatePart(name, parameters[0]);
                    var amount = Scalar(parameters[1], depth);
                    var date = Scalar(parameters[2], depth);
                    Require(amount, ViewValue.Number, name);
                    RequireTemporal(date, name);
                    return new CompiledViewExpression($"DATEADD({part}, {amount.Sql}, {date.Sql})", date.Value == ViewValue.Null ? ViewValue.Date : date.Value, null);
                }

                case "DATEDIFF":
                {
                    if (parameters.Count != 3)
                    {
                        throw Count(name, 3, 3, parameters.Count);
                    }

                    var part = DatePart(name, parameters[0]);
                    var from = Scalar(parameters[1], depth);
                    var to = Scalar(parameters[2], depth);
                    RequireTemporal(from, name);
                    RequireTemporal(to, name);
                    _ = Unify([from, to], "DATEDIFF's two values");
                    return new CompiledViewExpression($"DATEDIFF({part}, {from.Sql}, {to.Sql})", ViewValue.Number, null);
                }

                case "DATEPART":
                {
                    if (parameters.Count != 2)
                    {
                        throw Count(name, 2, 2, parameters.Count);
                    }

                    var part = DatePart(name, parameters[0]);
                    var date = Scalar(parameters[1], depth);
                    RequireTemporal(date, name);
                    return new CompiledViewExpression($"DATEPART({part}, {date.Sql})", ViewValue.Number, null);
                }

                case "YEAR" or "MONTH" or "DAY":
                {
                    var args = Arguments(name, parameters, 1, 1, depth);
                    Require(args[0], ViewValue.Date, name);
                    return new CompiledViewExpression($"{name}({args[0].Sql})", ViewValue.Number, null);
                }

                case "EOMONTH":
                {
                    var args = Arguments(name, parameters, 1, 2, depth);
                    Require(args[0], ViewValue.Date, name);
                    if (args.Count == 2)
                    {
                        Require(args[1], ViewValue.Number, name);
                    }

                    return new CompiledViewExpression($"EOMONTH({string.Join(", ", args.Select(a => a.Sql))})", ViewValue.Date, null);
                }

                case "STRING_ESCAPE":
                {
                    if (parameters.Count != 2)
                    {
                        throw Count(name, 2, 2, parameters.Count);
                    }

                    var value = Scalar(parameters[0], depth);
                    Require(value, ViewValue.Text, name);
                    return parameters[1] is StringLiteral { Value: var type } && string.Equals(type, "json", StringComparison.OrdinalIgnoreCase)
                        ? new CompiledViewExpression($"STRING_ESCAPE({value.Sql}, 'json')", ViewValue.Text, null)
                        : throw new ViewExpressionException("gives STRING_ESCAPE a type other than 'json', the one SQL Server escapes text for; write STRING_ESCAPE(<text>, 'json')");
                }

                default:
                    throw new ViewExpressionException(
                        $"calls {(name.Length == 0 ? "a function" : name)}, which is not one a view's expression may call; it may call {string.Join(", ", Functions)}, and in a view that groups its rows {string.Join(", ", Aggregates)}");
            }
        }

        /// <summary>
        /// An aggregate of the rows of a group (<see cref="Aggregates"/>), for a view that groups its rows: <c>COUNT</c> as
        /// <c>COUNT_BIG</c>, so a count never overflows; <c>MAX</c> and <c>MIN</c> of text by code point
        /// (<see cref="CodePointCollation"/>), whatever the database's collation; <c>STRING_AGG</c> over text that may grow
        /// past 8,000 bytes, in the order <c>WITHIN GROUP</c> gives (text ordered by code point), or none.
        /// </summary>
        private CompiledViewExpression Aggregate(FunctionCall call, string name, int depth)
        {
            if (groupedBy is null)
            {
                throw new ViewExpressionException(
                    $"calls {name}, an aggregate of the rows of a group, where one row is read (a view without groupBy, or its where, which keeps or leaves each row before it is grouped); give the view groupBy to aggregate its rows");
            }

            if (_inAggregate > 0)
            {
                throw new ViewExpressionException($"calls {name} inside another aggregate, which SQL Server does not take; aggregate the rows of a group once");
            }

            if (call.OverClause is not null)
            {
                throw new ViewExpressionException($"calls {name} over a window, which a view's expression may not; a grouped view aggregates the rows of its group");
            }

            if (call.WithinGroupClause is not null && name != "STRING_AGG")
            {
                throw new ViewExpressionException($"orders {name} WITHIN GROUP, which only STRING_AGG takes");
            }

            if (call.TrimOptions is not null || call.JsonParameters is { Count: > 0 })
            {
                throw new ViewExpressionException($"calls {name} with options a view's expression does not take; give it its arguments alone");
            }

            var distinct = call.UniqueRowFilter == UniqueRowFilter.Distinct;
            if (distinct && name == "STRING_AGG")
            {
                throw new ViewExpressionException("calls STRING_AGG with DISTINCT, which SQL Server does not take; aggregate the distinct values in a view of their own, grouped by them");
            }

            IList<ScalarExpression> parameters = call.Parameters ?? [];
            var expected = name == "STRING_AGG" ? 2 : 1;
            if (parameters.Count != expected)
            {
                throw Count(name, expected, expected, parameters.Count);
            }

            var all = distinct ? "DISTINCT " : string.Empty;
            _inAggregate++;
            try
            {
                if (name is "COUNT" or "COUNT_BIG" && parameters[0] is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard })
                {
                    return distinct
                        ? throw new ViewExpressionException($"counts DISTINCT *, which SQL Server does not take; count the distinct values of a column: {name}(DISTINCT <column>)")
                        : new CompiledViewExpression("COUNT_BIG(*)", ViewValue.Number, null);
                }

                var value = Scalar(parameters[0], depth);
                if (value.Value == ViewValue.Null)
                {
                    throw new ViewExpressionException($"gives {name} NULL alone, which SQL Server aggregates as no type at all and refuses; aggregate a column, or convert the value first");
                }

                switch (name)
                {
                    case "COUNT" or "COUNT_BIG":
                        return new CompiledViewExpression($"COUNT_BIG({all}{value.Sql})", ViewValue.Number, null);
                    case "MAX" or "MIN":
                        if (value.Value == ViewValue.UniqueId)
                        {
                            throw new ViewExpressionException($"gives {name} a GUID, which SQL Server does not order; convert it to text first, such as TRY_CAST(... AS nvarchar(36))");
                        }

                        // Text is the greatest or least by code point, read back in the database's collation, so a
                        // view comparing it with another column meets no collation conflict.
                        return new CompiledViewExpression(
                            value.Value == ViewValue.Text
                                ? $"CAST({name}({all}{ByCodePoint(value.Sql)}) AS nvarchar(max)) COLLATE DATABASE_DEFAULT"
                                : $"{name}({all}{value.Sql})",
                            value.Value,
                            null);
                    case "SUM" or "AVG":
                        Require(value, ViewValue.Number, name);
                        return new CompiledViewExpression($"{name}({all}{value.Sql})", ViewValue.Number, null);
                    default:
                    {
                        Require(value, ViewValue.Text, name);
                        if (parameters[1] is not StringLiteral separator)
                        {
                            throw new ViewExpressionException("gives STRING_AGG a separator that is not a text in quotes, such as STRING_AGG(Mnemonic, ', ')");
                        }

                        var sql = $"STRING_AGG(CAST({value.Sql} AS nvarchar(max)), N'{separator.Value.Replace("'", "''", StringComparison.Ordinal)}')";
                        if (call.WithinGroupClause?.OrderByClause?.OrderByElements is { Count: > 0 } order)
                        {
                            var items = new List<string>(order.Count);
                            var compared = new List<string>(order.Count);
                            foreach (var element in order)
                            {
                                var by = Scalar(element.Expression, depth);
                                if (by.Value == ViewValue.Null)
                                {
                                    throw new ViewExpressionException("orders STRING_AGG by NULL alone, which orders nothing; order it by a column");
                                }

                                var direction = element.SortOrder switch
                                {
                                    SortOrder.Descending => " DESC",
                                    SortOrder.Ascending => " ASC",
                                    _ => string.Empty,
                                };
                                var item = by.Value == ViewValue.Text ? ByCodePoint(by.Sql) : by.Sql;
                                items.Add(item + direction);
                                compared.Add(item + (element.SortOrder == SortOrder.Descending ? " DESC" : string.Empty));
                            }

                            sql += $" WITHIN GROUP (ORDER BY {string.Join(", ", items)})";
                            Orders.Add(string.Join(", ", compared));
                        }

                        return new CompiledViewExpression(sql, ViewValue.Text, null);
                    }
                }
            }
            finally
            {
                _inAggregate--;
            }
        }

        /// <summary>A condition, as CASE, IIF and a view's <c>where</c> take one.</summary>
        public string Boolean(BooleanExpression node, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ViewExpressionException(string.Create(CultureInfo.InvariantCulture, $"nests deeper than {MaxDepth}; split it over several columns"));
            }

            var next = depth + 1;
            switch (node)
            {
                case BooleanComparisonExpression comparison:
                {
                    var symbol = comparison.ComparisonType switch
                    {
                        BooleanComparisonType.Equals => "=",
                        BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => "<>",
                        BooleanComparisonType.GreaterThan => ">",
                        BooleanComparisonType.LessThan => "<",
                        BooleanComparisonType.GreaterThanOrEqualTo => ">=",
                        BooleanComparisonType.LessThanOrEqualTo => "<=",
                        _ => throw new ViewExpressionException("compares with an operator a view's expression does not take; it takes =, <>, <, <=, > and >="),
                    };
                    var left = Scalar(comparison.FirstExpression, next);
                    var right = Scalar(comparison.SecondExpression, next);
                    _ = Unify([left, right], $"the two sides of {symbol}");
                    return $"({left.Sql} {symbol} {right.Sql})";
                }

                case BooleanBinaryExpression binary:
                    return $"({Boolean(binary.FirstExpression, next)} {(binary.BinaryExpressionType == BooleanBinaryExpressionType.And ? "AND" : "OR")} {Boolean(binary.SecondExpression, next)})";
                case BooleanNotExpression not:
                    return $"(NOT {Boolean(not.Expression, next)})";
                case BooleanParenthesisExpression parenthesis:
                    return $"({Boolean(parenthesis.Expression, next)})";
                case BooleanIsNullExpression isNull:
                    return $"({Scalar(isNull.Expression, next).Sql} IS {(isNull.IsNot ? "NOT " : string.Empty)}NULL)";
                case LikePredicate like:
                {
                    if (like.OdbcEscape)
                    {
                        throw new ViewExpressionException("writes LIKE's escape the ODBC way; write ESCAPE '\\' after the pattern");
                    }

                    var value = Scalar(like.FirstExpression, next);
                    var pattern = Scalar(like.SecondExpression, next);
                    Require(value, ViewValue.Text, "LIKE");
                    Require(pattern, ViewValue.Text, "LIKE");
                    var escape = string.Empty;
                    if (like.EscapeExpression is not null)
                    {
                        escape = like.EscapeExpression is StringLiteral { Value.Length: 1 } character
                            ? " ESCAPE N'" + character.Value.Replace("'", "''", StringComparison.Ordinal) + "'"
                            : throw new ViewExpressionException("gives LIKE an escape that is not one character in quotes, such as ESCAPE '\\'");
                    }

                    return $"({value.Sql} {(like.NotDefined ? "NOT " : string.Empty)}LIKE {pattern.Sql}{escape})";
                }

                case InPredicate @in:
                {
                    if (@in.Subquery is not null)
                    {
                        throw new ViewExpressionException("compares with IN over a subquery, which a view's expression may not: it reads the dimensions it joins and nothing else; list the values");
                    }

                    var value = Scalar(@in.Expression, next);
                    var listed = @in.Values.Select(v => Scalar(v, next)).ToList();
                    _ = Unify([value, .. listed], "IN's value and the values it lists");
                    return $"({value.Sql} {(@in.NotDefined ? "NOT " : string.Empty)}IN ({string.Join(", ", listed.Select(v => v.Sql))}))";
                }

                case BooleanTernaryExpression between:
                {
                    var value = Scalar(between.FirstExpression, next);
                    var low = Scalar(between.SecondExpression, next);
                    var high = Scalar(between.ThirdExpression, next);
                    _ = Unify([value, low, high], "BETWEEN's value and its bounds");
                    return $"({value.Sql} {(between.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween ? "NOT " : string.Empty)}BETWEEN {low.Sql} AND {high.Sql})";
                }

                default:
                    throw new ViewExpressionException($"tests {Describe(node)}, which a view's expression may not; it compares values, with AND, OR, NOT, IS NULL, LIKE, IN and BETWEEN");
            }
        }

        private List<CompiledViewExpression> Arguments(string name, IList<ScalarExpression> parameters, int min, int max, int depth)
        {
            if (parameters.Count < min || parameters.Count > max)
            {
                throw Count(name, min, max, parameters.Count);
            }

            return parameters.Select(p => Scalar(p, depth)).ToList();
        }

        /// <summary>
        /// A length a call takes, null where it is negative: the call then gives null. The argument is guarded, not the call,
        /// since SQL Server folds a call of constants when it compiles the statement, and raises there, whatever a CASE
        /// around it says.
        /// </summary>
        private static string NotNegative(string length) => $"CASE WHEN {length} < 0 THEN NULL ELSE {length} END";

        private static ViewExpressionException Count(string name, int min, int max, int given)
            => new(string.Create(CultureInfo.InvariantCulture,
                $"calls {name} with {given} argument(s); it takes {(min == max ? min.ToString(CultureInfo.InvariantCulture) : max >= 254 ? $"{min} or more" : $"{min} to {max}")}"));

        private static string DatePart(string function, ScalarExpression part)
            => part is ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { Count: 1 } ids } && DateParts.Contains(ids[0].Value)
                ? ids[0].Value.ToLowerInvariant()
                : throw new ViewExpressionException(
                    $"gives {function} a part it does not take; its first argument is a part of a date or a time, such as day, month, year, hour or minute");

        private static void Require(CompiledViewExpression value, ViewValue wanted, string what)
        {
            if (value.Value != ViewValue.Null && value.Value != wanted)
            {
                throw new ViewExpressionException(
                    $"gives {what} {Word(value.Value)} ({Shown(value)}), where it takes {Word(wanted)}; SQL Server would convert it implicitly and fail the whole read at the first value it cannot, so convert it first, such as TRY_CAST(... AS {Example(wanted)})");
            }
        }

        private static void RequireTemporal(CompiledViewExpression value, string what)
        {
            if (value.Value is not (ViewValue.Null or ViewValue.Date or ViewValue.Time))
            {
                throw new ViewExpressionException(
                    $"gives {what} {Word(value.Value)} ({Shown(value)}), where it takes a date or a time of day; convert it first, such as TRY_CAST(... AS datetime2(3))");
            }
        }

        /// <summary>The one kind of value <paramref name="values"/> give, a NULL meeting any; refused where two differ.</summary>
        private static ViewValue Unify(IReadOnlyList<CompiledViewExpression> values, string what)
        {
            var kind = ViewValue.Null;
            CompiledViewExpression? first = null;
            foreach (var value in values)
            {
                if (value.Value == ViewValue.Null)
                {
                    continue;
                }

                if (first is null)
                {
                    first = value;
                    kind = value.Value;
                    continue;
                }

                if (value.Value != kind)
                {
                    throw new ViewExpressionException(
                        $"mixes {Word(kind)} ({Shown(first)}) and {Word(value.Value)} ({Shown(value)}) in {what}; SQL Server would convert one to the other implicitly and fail the whole read at the first value it cannot, so convert one first");
                }
            }

            return kind;
        }

        private static string Shown(CompiledViewExpression value) => value.Column?.Named ?? "a value computed";

        private static string Example(ViewValue wanted) => wanted switch
        {
            ViewValue.Number => "float",
            ViewValue.Date => "datetime2(3)",
            ViewValue.Time => "time(0)",
            ViewValue.UniqueId => "uniqueidentifier",
            _ => "nvarchar(256)",
        };

        private static string TypeText(DataTypeReference type)
        {
            if (type is not SqlDataTypeReference sql)
            {
                throw new ViewExpressionException($"converts to {type?.Name?.BaseIdentifier?.Value ?? "a type"}, which is not one of SQL Server's own types a view converts to; the types are {string.Join(", ", DimensionConversions.Types)}");
            }

            var name = sql.SqlDataTypeOption.ToString().ToLowerInvariant();
            if (sql.Parameters is not { Count: > 0 } parameters)
            {
                return name;
            }

            var values = parameters.Select(p => p is MaxLiteral ? "max" : p.Value).ToList();
            return $"{name}({string.Join(", ", values)})";
        }

        private static string Describe(TSqlFragment node) => node switch
        {
            ScalarSubquery => "a subquery",
            VariableReference => "a variable",
            GlobalVariableExpression => "a system value (@@...)",
            ParameterlessCall => "a value of the session or the clock (CURRENT_TIMESTAMP, USER and the like)",
            AtTimeZoneCall => "AT TIME ZONE",
            TryParseCall or ParseCall => "PARSE, which reads by a culture",
            ExistsPredicate => "EXISTS, which reads another table",
            DistinctPredicate => "IS DISTINCT FROM",
            FullTextPredicate => "a full-text search",
            _ => $"a {Regex.Replace(node.GetType().Name, "([a-z])([A-Z])", "$1 $2", RegexOptions.None, TimeSpan.FromSeconds(1)).ToLowerInvariant()}",
        };
    }
}

/// <summary>
/// The module's conversions (osdu/docs/reference/flow/dimension-views.md): every conversion a view makes, whichever of
/// <c>CAST</c>, <c>CONVERT</c>, <c>TRY_CAST</c> and <c>TRY_CONVERT</c> the document writes, or the <c>dataType</c> it gives
/// a column. None fails a read and none changes a value without saying so: a value it cannot convert is null, and the check
/// counts it. Each is written for the text dimensions hold, and gives the same answer whatever the reading session's
/// language and date format: a date is read as ISO 8601 alone, a fraction never becomes a whole number, and a text never
/// loses its end.
/// </summary>
public static class DimensionConversions
{
    /// <summary>The types a view converts to, as the reference lists them.</summary>
    public static IReadOnlyList<string> Types { get; } =
    [
        "bit", "tinyint", "smallint", "int", "bigint", "decimal(p,s)", "numeric(p,s)", "float", "real", "date", "time(n)", "datetime2(n)",
        "datetimeoffset(n)", "uniqueidentifier", "nvarchar(n)", "nvarchar(max)",
    ];

    /// <summary>The longest text a view converts to a bounded <c>nvarchar</c>.</summary>
    public const int MaxTextLength = 4000;

    /// <summary>A data type as a document writes it, checked against the types a view converts to.</summary>
    /// <exception cref="ViewExpressionException">It is not one of them; the message says which to use.</exception>
    public static SqlDataType Parse(string written)
    {
        ArgumentNullException.ThrowIfNull(written);
        SqlDataType type;
        try
        {
            type = SqlDataType.Parse(written);
        }
        catch (SqlFlowException ex)
        {
            throw new ViewExpressionException($"converts to '{written}', which is not a data type: {ex.Message}", ex);
        }

        switch (type.BaseType)
        {
            case "bit" or "tinyint" or "smallint" or "int" or "bigint" or "date" or "uniqueidentifier":
                return type.Length is null && type.Precision is null && type.Scale is null
                    ? type
                    : throw new ViewExpressionException($"converts to '{written}', and {type.BaseType} takes no length or precision");
            case "float" or "real":
                return type.Length is null
                    ? type
                    : throw new ViewExpressionException($"converts to '{written}'; write float or real alone: float holds 15 digits, real 7");
            case "decimal" or "numeric":
            {
                var precision = type.Precision ?? 18;
                var scale = type.Scale ?? 0;
                return precision is >= 1 and <= 38 && scale >= 0 && scale <= precision
                    ? type with { Precision = precision, Scale = scale }
                    : throw new ViewExpressionException($"converts to '{written}'; a {type.BaseType} holds 1 to 38 digits, of which 0 up to all are after the point");
            }

            case "time" or "datetime2" or "datetimeoffset":
            {
                var scale = type.Scale ?? 7;
                return scale is >= 0 and <= 7
                    ? type with { Scale = scale }
                    : throw new ViewExpressionException($"converts to '{written}'; a {type.BaseType} keeps 0 to 7 digits of a second");
            }

            case "nvarchar":
                if (type.Length is null)
                {
                    throw new ViewExpressionException($"converts to '{written}'; give nvarchar its length, such as nvarchar(256), or nvarchar(max)");
                }

                return type.IsMax || type.Length is >= 1 and <= MaxTextLength
                    ? type
                    : throw new ViewExpressionException(string.Create(CultureInfo.InvariantCulture, $"converts to '{written}'; an nvarchar holds 1 to {MaxTextLength} characters, or max"));
            case "varchar" or "char" or "nchar" or "text" or "ntext":
                throw new ViewExpressionException(
                    $"converts to '{written}'; varchar and char would turn every character outside their code page into ? without a word, and nchar pads: use nvarchar(n)");
            case "datetime" or "smalldatetime":
                throw new ViewExpressionException($"converts to '{written}', which rounds and stops at 1753; use datetime2(n), which does neither");
            default:
                throw new ViewExpressionException($"converts to '{written}', which is not a type a view converts to; the types are {string.Join(", ", Types)}");
        }
    }

    /// <summary>The kind of value a type holds.</summary>
    public static ViewValue ValueOf(SqlDataType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.BaseType switch
        {
            "nvarchar" => ViewValue.Text,
            "date" or "datetime2" or "datetimeoffset" => ViewValue.Date,
            "time" => ViewValue.Time,
            "uniqueidentifier" => ViewValue.UniqueId,
            _ => ViewValue.Number,
        };
    }

    /// <summary>
    /// The SQL that converts <paramref name="value"/>, which gives <paramref name="from"/>, to <paramref name="to"/> (a type
    /// <see cref="Parse"/> checked): null for a value it cannot convert, never an error.
    /// </summary>
    /// <exception cref="ViewExpressionException">No value of <paramref name="from"/> converts to <paramref name="to"/>.</exception>
    public static string Convert(string value, ViewValue from, SqlDataType to)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(to);
        var target = to.Render();
        var text = from is ViewValue.Text or ViewValue.Null;
        switch (to.BaseType)
        {
            case "bit":
                return from switch
                {
                    ViewValue.Text or ViewValue.Null =>
                        $"CASE UPPER(LTRIM(RTRIM({value}))) WHEN N'TRUE' THEN CAST(1 AS bit) WHEN N'1' THEN CAST(1 AS bit) WHEN N'FALSE' THEN CAST(0 AS bit) WHEN N'0' THEN CAST(0 AS bit) END",
                    ViewValue.Number => $"CASE WHEN {value} = 1 THEN CAST(1 AS bit) WHEN {value} = 0 THEN CAST(0 AS bit) END",
                    _ => throw Refused(from, to),
                };
            case "tinyint" or "smallint" or "int" or "bigint":
                // A whole number in any form ('7', '7.0', '7E0'), read exactly through decimal; a fraction is no whole number.
                return from switch
                {
                    ViewValue.Text or ViewValue.Null =>
                        $"TRY_CAST(CASE WHEN {Exact(value)} % 1 = 0 THEN {Exact(value)} END AS {target})",
                    ViewValue.Number => $"TRY_CAST(CASE WHEN {value} = FLOOR({value}) THEN {value} END AS {target})",
                    _ => throw Refused(from, to),
                };
            case "float" or "real":
                return from is ViewValue.Text or ViewValue.Null or ViewValue.Number ? $"TRY_CAST({value} AS {target})" : throw Refused(from, to);
            case "decimal" or "numeric":
                // TRY_CAST to decimal reads no exponent ('1.5E3'), so a text it cannot read is read through float.
                return from switch
                {
                    ViewValue.Text or ViewValue.Null => $"COALESCE(TRY_CAST({value} AS {target}), TRY_CAST(TRY_CAST({value} AS float) AS {target}))",
                    ViewValue.Number => $"TRY_CAST({value} AS {target})",
                    _ => throw Refused(from, to),
                };
            case "datetimeoffset":
                return from is ViewValue.Text or ViewValue.Null or ViewValue.Date ? $"TRY_CAST({Instant(value, text)} AS {target})" : throw Refused(from, to);
            case "datetime2":
                // As the instant in UTC: a cast would keep the clock of an offset and drop the offset.
                return from is ViewValue.Text or ViewValue.Null or ViewValue.Date
                    ? $"TRY_CAST(SWITCHOFFSET({Instant(value, text)}, '+00:00') AS {target})"
                    : throw Refused(from, to);
            case "date":
                return from is ViewValue.Text or ViewValue.Null or ViewValue.Date ? $"TRY_CAST({Instant(value, text)} AS {target})" : throw Refused(from, to);
            case "time":
                return from switch
                {
                    ViewValue.Text or ViewValue.Null or ViewValue.Date => $"TRY_CAST({Instant(value, text)} AS {target})",
                    ViewValue.Time => $"TRY_CAST({value} AS {target})",
                    _ => throw Refused(from, to),
                };
            case "uniqueidentifier":
                return from switch
                {
                    ViewValue.Text or ViewValue.Null => $"TRY_CAST({value} AS {target})",
                    ViewValue.UniqueId => value,
                    _ => throw Refused(from, to),
                };
            case "nvarchar":
            {
                // A number keeps every digit (a cast keeps six of a float), a date and a time are written as ISO 8601.
                var written = from switch
                {
                    ViewValue.Text or ViewValue.Null => value,
                    ViewValue.Number => $"CONVERT(nvarchar(4000), {value}, 3)",
                    ViewValue.Date or ViewValue.Time => $"CONVERT(nvarchar(4000), {value}, 126)",
                    _ => $"CONVERT(nvarchar(36), {value})",
                };
                if (to.IsMax)
                {
                    return $"CAST({written} AS nvarchar(max))";
                }

                // A cast cuts a longer text at the length; a text that does not fit is no value instead. DATALENGTH counts
                // the trailing spaces LEN leaves out.
                var bytes = (to.Length ?? 1) * 2;
                return string.Create(CultureInfo.InvariantCulture, $"CASE WHEN DATALENGTH({written}) <= {bytes} THEN CAST({written} AS {target}) END");
            }

            default:
                throw Refused(from, to);
        }
    }

    /// <summary>A text read exactly as a number of ten places, through float when it is written with an exponent.</summary>
    private static string Exact(string value)
        => $"COALESCE(TRY_CAST({value} AS decimal(38, 10)), TRY_CAST(TRY_CAST({value} AS float) AS decimal(38, 10)))";

    /// <summary>
    /// A value as an instant with its offset: a text read as ISO 8601 alone (style 127), whatever the session's date format,
    /// a date as itself, in UTC when it has no offset.
    /// </summary>
    private static string Instant(string value, bool text)
        => text ? $"TRY_CONVERT(datetimeoffset(7), {value}, 127)" : $"TRY_CAST({value} AS datetimeoffset(7))";

    private static ViewExpressionException Refused(ViewValue from, SqlDataType to)
        => new($"converts {DimensionViewExpressions.Word(from)} to {to.Render()}, which no value of it converts to");
}
