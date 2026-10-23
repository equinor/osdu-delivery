using System.Collections;
using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Maps a dimension flow's <c>target</c> and <c>views</c> (osdu/docs/reference/flow/dimension-views.md) and refuses,
/// where the document is read, everything a build could not write or would write wrongly: a join to a dimension the flow
/// does not declare, on a column holding no key, to keys it would not meet, or to a dimension of several rows a key,
/// which would repeat the view's rows; a column name used twice; and an expression or a data type a view may not use. A
/// view is settled by its document alone: the mapper lays it out (<see cref="DimensionViews"/>), so what a build writes
/// is known before it runs.
/// </summary>
internal static class DimensionViewMapper
{
    /// <summary>The settings a view's column takes beside its bare form.</summary>
    internal static IReadOnlyList<string> ColumnSettings { get; } = ["expression", "dataType", "description"];

    /// <summary>The module's database as the document names it, checked as a delivery flow's source connection is: references only.</summary>
    public static DimensionTarget? MapTarget(DimensionTargetYaml? declared, string source)
    {
        if (declared is null)
        {
            return null;
        }

        var connection = FlowMapper.Require(declared.Connection, "target.connection", source).Trim();
        IngestionConnection.CheckDeclared(connection, source, "target.connection");
        return new DimensionTarget(connection);
    }

    public static IReadOnlyList<DimensionViewSpec> MapViews(
        List<DimensionViewYaml>? declared, DimensionTarget? target, string flow, IReadOnlyList<DimensionSpec> dimensions, string source)
    {
        if (declared is null || declared.Count == 0)
        {
            return [];
        }

        if (target is null)
        {
            throw new FlowValidationException(
                $"{source}: views needs target.connection: the module's database as the pipelines reading the views name it (target: {{ connection: ${{env:{OsduModuleDatabase.CatalogConnectionVariable}}} }}), so SQLFlow orders those pipelines after this flow, and a build checks it reaches the module's own database.");
        }

        if (declared.Count > DimensionViewSpec.MaxViews)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: views lists {declared.Count} views; one flow declares at most {DimensionViewSpec.MaxViews}."));
        }

        var views = new List<DimensionViewSpec>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var view = MapView(declared[i], i, flow, dimensions, source);
            if (views.FirstOrDefault(v => string.Equals(v.Name, view.Name, StringComparison.OrdinalIgnoreCase)) is { } twice)
            {
                throw new FlowValidationException(
                    string.Create(CultureInfo.InvariantCulture, $"{source}: views[{i}] is named '{view.Name}', as views '{twice.Name}' is, ignoring case; each view is a view of its own name in the database."));
            }

            views.Add(view);
        }

        return views;
    }

    private static DimensionViewSpec MapView(DimensionViewYaml? v, int index, string flow, IReadOnlyList<DimensionSpec> dimensions, string source)
    {
        var at = string.Create(CultureInfo.InvariantCulture, $"views[{index}]");
        if (v is null)
        {
            throw new FlowValidationException($"{source}: {at} is empty.");
        }

        var name = FlowMapper.Require(v.Name, at + ".name", source).Trim();
        if (!DimensionViewSpec.IsName(name))
        {
            throw new FlowValidationException(
                $"{source}: {at}.name '{name}' is not a view name: a letter, then letters, digits and underscores, at most {DimensionViewSpec.MaxNameLength}. The view is {DimensionViewSpec.Prefix}<name> in the osdu schema.");
        }

        var where = $"{at} '{name}'";
        var description = Description(v.Description, $"{where}: description", source);
        var fromName = FlowMapper.Require(v.From, $"{where}: from", source).Trim();
        var from = Dimension(dimensions, fromName)
            ?? throw new FlowValidationException($"{source}: {where}: from names '{fromName}', which is not a dimension of the flow; its dimensions are {Listed(dimensions)}.");

        // The tables the expressions read: the from dimension's bare, each join's by its alias.
        var scope = new ViewScope();
        scope.Add(ViewScope.FromAlias, from.Name, DimensionViews.ColumnsOf(from, ViewScope.FromAlias, DimensionViews.FromSqlAlias));
        var read = new Dictionary<string, DimensionSpec>(StringComparer.OrdinalIgnoreCase) { [ViewScope.FromAlias] = from };
        var joins = new List<DimensionViewJoin>();
        var joinSql = new List<DimensionViews.JoinSql>();
        var joinDefinitions = new List<DimensionViewJoinDefinition>();
        var joinedTables = new List<(string Alias, string SqlAlias, DimensionSpec Dimension)>();
        var declaredJoins = v.Join ?? [];
        if (declaredJoins.Count > DimensionViewSpec.MaxJoins)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: join lists {declaredJoins.Count} joins; a view makes at most {DimensionViewSpec.MaxJoins}."));
        }

        for (var i = 0; i < declaredJoins.Count; i++)
        {
            var place = string.Create(CultureInfo.InvariantCulture, $"{where}: join[{i}]");
            var join = declaredJoins[i] ?? throw new FlowValidationException($"{source}: {place} is empty; a join is {{ on: <column>, to: <dimension>, as: <alias> }}.");
            var on = FlowMapper.Require(join.On, place + ".on", source).Trim();
            var toName = FlowMapper.Require(join.To, place + ".to", source).Trim();
            var to = Dimension(dimensions, toName)
                ?? throw new FlowValidationException($"{source}: {place}.to names '{toName}', which is not a dimension of the flow; its dimensions are {Listed(dimensions)}. A view joins the dimensions of its own flow, so one run settles every table it reads.");
            var alias = join.As is null ? to.Name : join.As.Trim();
            if (!DimensionViewSpec.IsName(alias))
            {
                throw new FlowValidationException(
                    $"{source}: {place}.as '{alias}' is not an alias: a letter, then letters, digits and underscores, at most {DimensionViewSpec.MaxNameLength}.{(join.As is null ? $" The alias is the dimension's name unless the join gives one; give it one: as: <alias>." : string.Empty)}");
            }

            if (read.ContainsKey(alias))
            {
                throw new FlowValidationException(
                    $"{source}: {place} reads {to.Name} as {alias}, an alias an earlier join has, ignoring case; give each join of the same dimension its own: as: <alias>.");
            }

            // The column joined on: one of the from dimension's, bare, or of an earlier join's, by its alias.
            var parts = on.Split('.');
            if (parts.Length > 2 || parts.Any(p => p.Trim().Length == 0))
            {
                throw new FlowValidationException(
                    $"{source}: {place}.on '{on}' is not a column: it is a column of the from dimension, bare (CurveUnitID), or of an earlier join, by its alias (WellLog.SamplingDomainTypeID).");
            }

            var (onAlias, onColumn) = parts.Length == 1 ? (ViewScope.FromAlias, parts[0].Trim()) : (parts[0].Trim(), parts[1].Trim());
            ViewColumnSource onSource;
            try
            {
                onSource = scope.Resolve(onAlias, onColumn);
            }
            catch (ViewExpressionException ex)
            {
                throw new FlowValidationException($"{source}: {place}.on {ex.Message}.", ex);
            }

            var holder = read[onAlias];
            var (key, why) = DimensionViews.KeyOf(holder, onSource.Column);
            if (key is null)
            {
                throw new FlowValidationException($"{source}: {place}.on '{on}' holds no key: {why}.");
            }

            var meets = DimensionViews.Meets(key, DimensionViews.KeyOf(to));
            if (meets is not null)
            {
                throw new FlowValidationException($"{source}: {place} joins '{on}' to {to.Name}, and {meets}.");
            }

            if (DimensionViews.RowsPerKey(to))
            {
                throw new FlowValidationException(
                    $"{source}: {place} joins {to.Name}, whose table holds a row per {(to.Elements is not null ? "element" : "value it collects")} of each key, so a join to it would repeat the view's rows. Make it the view's from dimension, or join a dimension of the same key without {(to.Elements is not null ? "elements" : "a collected attribute")}.");
            }

            var sqlAlias = string.Create(CultureInfo.InvariantCulture, $"j{i + 1}");
            var table = DimensionTables.NameOf(to.Name);
            joins.Add(new DimensionViewJoin(on, to.Name, alias));
            joinSql.Add(new DimensionViews.JoinSql(table, sqlAlias, to.KeyColumn, onSource.Sql));
            joinDefinitions.Add(new DimensionViewJoinDefinition(alias, to.Name, on, table, sqlAlias, onSource.Sql));
            joinedTables.Add((alias, sqlAlias, to));
            scope.Add(alias, to.Name, DimensionViews.ColumnsOf(to, alias, sqlAlias));
            read[alias] = to;
        }

        var (columns, specs, applies) = v.Columns is { Count: > 0 } listed
            ? ListedColumns(listed, scope, where, source)
            : DefaultColumns(from, joinedTables, where, source);

        string? whereSql = null;
        var condition = v.Where?.Trim();
        if (v.Where is not null)
        {
            try
            {
                whereSql = DimensionViewExpressions.CompileCondition(condition!, scope);
            }
            catch (ViewExpressionException ex)
            {
                throw new FlowValidationException($"{source}: {where}: where {Quoted(condition!)} {ex.Message}.", ex);
            }
        }

        var fromTable = DimensionTables.NameOf(from.Name);
        var fromSql = DimensionViews.FromSql(fromTable, joinSql, applies);
        var viewName = DimensionViewSpec.Prefix + name;
        var all = new List<DimensionViewColumnDefinition>
        {
            new("partition", "nvarchar(256)", $"[{DimensionViews.FromSqlAlias}].[partition]", null, null, "The partition the row was read in.", null),
            new("id", "bigint", $"[{DimensionViews.FromSqlAlias}].[id]", null, null, $"The number of the row of {fromTable} the view's row stands for: the view's key.", null),
        };
        all.AddRange(columns);
        var createSql = DimensionViews.CreateSql(flow, viewName, all, fromSql, whereSql);
        var tables = new List<string> { fromTable };
        tables.AddRange(joinDefinitions.Select(j => j.Table).Where(t => !tables.Contains(t, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase));
        return new DimensionViewSpec
        {
            Name = name,
            Description = description,
            From = from.Name,
            Joins = joins,
            Columns = specs,
            Where = condition,
            Definition = new DimensionViewDefinition(viewName, tables, all, joinDefinitions, fromSql, createSql, DimensionViews.HashOf(createSql), whereSql),
        };
    }

    /// <summary>The columns the document lists, each an expression compiled over the view's tables and converted to its data type.</summary>
    private static (List<DimensionViewColumnDefinition> Columns, List<DimensionViewColumnSpec> Specs, List<string> Applies) ListedColumns(
        Dictionary<string, object?> listed, ViewScope scope, string where, string source)
    {
        if (listed.Count > DimensionViewSpec.MaxColumns)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: columns lists {listed.Count} columns; a view holds at most {DimensionViewSpec.MaxColumns} beside its partition and id."));
        }

        var columns = new List<DimensionViewColumnDefinition>(listed.Count);
        var specs = new List<DimensionViewColumnSpec>(listed.Count);
        var applies = new List<string>();
        foreach (var (written, value) in listed)
        {
            var name = written.Trim();
            var at = $"{where}: columns.{name}";
            if (!DimensionViewSpec.IsName(name))
            {
                throw new FlowValidationException(
                    $"{source}: {where}: columns.{written} is not a column name: a letter, then letters, digits and underscores, at most {DimensionViewSpec.MaxNameLength}.");
            }

            if (DimensionViewSpec.KeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new FlowValidationException(
                    $"{source}: {at} takes the name of a column every view begins with ({string.Join(" and ", DimensionViewSpec.KeyColumns)}); name it after what it holds.");
            }

            if (specs.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlowValidationException($"{source}: {at} is named twice, ignoring case; a view has one column of a name.");
            }

            var (expression, dataType, description) = ColumnOf(value, name, at, source);
            CompiledViewExpression compiled;
            try
            {
                compiled = DimensionViewExpressions.Compile(expression, scope);
            }
            catch (ViewExpressionException ex)
            {
                throw new FlowValidationException($"{source}: {at}: the expression {Quoted(expression)} {ex.Message}.", ex);
            }

            if (dataType is null)
            {
                columns.Add(new DimensionViewColumnDefinition(
                    name, compiled.Column?.SqlType ?? DimensionViewExpressions.TypeWord(compiled.Value), compiled.Sql, expression, null, description, null));
            }
            else
            {
                try
                {
                    var type = DimensionConversions.Parse(dataType);

                    // A value converted is computed once, so the conversion and the check read the same value: a column read
                    // as it is needs nothing computed.
                    var input = compiled.Sql;
                    if (compiled.Column is null)
                    {
                        var apply = string.Create(CultureInfo.InvariantCulture, $"c{applies.Count + 1}");
                        applies.Add($"CROSS APPLY (SELECT {compiled.Sql} AS [v]) AS [{apply}]");
                        input = $"[{apply}].[v]";
                    }

                    columns.Add(new DimensionViewColumnDefinition(
                        name, type.Render(), DimensionConversions.Convert(input, compiled.Value, type), expression, type.Render(), description, input));
                }
                catch (ViewExpressionException ex)
                {
                    throw new FlowValidationException($"{source}: {at}: dataType {ex.Message}.", ex);
                }
            }

            specs.Add(new DimensionViewColumnSpec(name, expression, dataType, description));
        }

        return (columns, specs, applies);
    }

    /// <summary>A column as the document writes it: an expression alone, or its settings.</summary>
    private static (string Expression, string? DataType, string? Description) ColumnOf(object? value, string name, string at, string source)
    {
        switch (value)
        {
            case string expression:
                return (expression.Trim(), null, null);
            case IDictionary<object, object?> settings:
            {
                var unknown = settings.Keys.Select(k => k?.ToString()).Where(k => !ColumnSettings.Contains(k, StringComparer.Ordinal)).ToList();
                if (unknown.Count > 0)
                {
                    throw new FlowValidationException(
                        $"{source}: {at} names {string.Join(", ", unknown)}; a column is an expression ({name}: TopDepth), or settings {{ {string.Join(", ", ColumnSettings)} }}.");
                }

                if (!settings.TryGetValue("expression", out var declared) || declared is not string expression || string.IsNullOrWhiteSpace(expression))
                {
                    throw new FlowValidationException(
                        $"{source}: {at} gives no expression; give the expression the column is computed by ({name}: {{ expression: TopDepth, dataType: float }}).");
                }

                var dataType = settings.TryGetValue("dataType", out var type) ? Scalar(type, at + ".dataType", source) : null;
                var description = settings.TryGetValue("description", out var text) ? Description(Scalar(text, at + ".description", source), at + ".description", source) : null;
                return (expression.Trim(), string.IsNullOrWhiteSpace(dataType) ? null : dataType.Trim(), description);
            }

            case IList:
                throw new FlowValidationException(
                    $"{source}: {at} is a list. An expression that starts with [ is a list in YAML, so quote it: {name}: '[Unit].[Name]'.");
            case null:
                throw new FlowValidationException($"{source}: {at} is empty; give the expression the column is computed by ({name}: TopDepth).");
            default:
                throw new FlowValidationException($"{source}: {at} is neither an expression nor settings {{ {string.Join(", ", ColumnSettings)} }}.");
        }
    }

    /// <summary>The columns of a view that lists none (<see cref="DimensionViews.DefaultColumns"/>), each a column read as it is.</summary>
    private static (List<DimensionViewColumnDefinition> Columns, List<DimensionViewColumnSpec> Specs, List<string> Applies) DefaultColumns(
        DimensionSpec from, IReadOnlyList<(string Alias, string SqlAlias, DimensionSpec Dimension)> joins, string where, string source)
    {
        var columns = new List<DimensionViewColumnDefinition>();
        var names = new HashSet<string>(DimensionViewSpec.KeyColumns, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, column) in DimensionViews.DefaultColumns(from, joins))
        {
            if (name.Length > DimensionViews.MaxSqlNameLength)
            {
                throw new FlowValidationException(
                    string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: the view would hold a column {name}, longer than the {DimensionViews.MaxSqlNameLength} characters SQL Server names a column by; give the join a shorter alias, or list the view's columns."));
            }

            if (!names.Add(name))
            {
                throw new FlowValidationException(
                    $"{source}: {where}: the view would hold two columns named {name}, ignoring case, one of them a joined column under its alias's prefix; give the join another alias, or list the view's columns.");
            }

            columns.Add(new DimensionViewColumnDefinition(name, column.SqlType, column.Sql, null, null, null, null));
        }

        return columns.Count > DimensionViewSpec.MaxColumns
            ? throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: the view would hold {columns.Count} columns, and a view holds at most {DimensionViewSpec.MaxColumns} beside its partition and id; list the columns it holds."))
            : (columns, [], []);
    }

    private static DimensionSpec? Dimension(IReadOnlyList<DimensionSpec> dimensions, string name)
        => dimensions.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string Listed(IReadOnlyList<DimensionSpec> dimensions)
        => dimensions.Count <= 20
            ? string.Join(", ", dimensions.Select(d => d.Name))
            : string.Join(", ", dimensions.Take(20).Select(d => d.Name)) + string.Create(CultureInfo.InvariantCulture, $" and {dimensions.Count - 20} more");

    private static string? Description(string? text, string at, string source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= DimensionViewSpec.MaxDescriptionLength
            ? trimmed
            : throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {at} is {trimmed.Length} characters; a description is at most {DimensionViewSpec.MaxDescriptionLength}."));
    }

    private static string? Scalar(object? value, string at, string source) => value switch
    {
        null => null,
        string text => text,
        IFormattable formattable when value is not IDictionary and not IList => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new FlowValidationException($"{source}: {at} must be text, not {(value is IList ? "a list" : "a map")}."),
    };

    /// <summary>An expression as a message quotes it, cut where it is long.</summary>
    private static string Quoted(string expression)
    {
        var line = expression.ReplaceLineEndings(" ");
        return "'" + (line.Length > 80 ? line[..80] + "..." : line) + "'";
    }
}
