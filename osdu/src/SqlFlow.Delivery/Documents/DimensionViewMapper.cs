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
/// which would repeat the view's rows; a join to a view that is not declared before it or does not group by one key; a
/// grouped view's column that reads a column it does not group by outside an aggregate; a column name used twice; and an
/// expression or a data type a view may not use. A view is settled by its document alone: the mapper lays it out
/// (<see cref="DimensionViews"/>), so what a build writes is known before it runs.
/// </summary>
internal static class DimensionViewMapper
{
    /// <summary>
    /// A view mapped earlier in the flow, as a later view may join it: by the one key it groups by, when it groups by one
    /// column holding a key that it lists bare (<see cref="JoinColumn"/>), with the columns a join reads, typed.
    /// </summary>
    /// <param name="Spec">The view.</param>
    /// <param name="JoinColumn">The column of the view a join compares, the key it groups by; null when the view cannot be joined.</param>
    /// <param name="Key">The key that column holds.</param>
    /// <param name="Columns">The view's columns as a join reads them: each name, the kind of value it gives and its type.</param>
    /// <param name="WhyNot">Why the view cannot be joined, when it cannot.</param>
    private sealed record MappedView(
        DimensionViewSpec Spec, string? JoinColumn, DimensionJoinKey? Key, IReadOnlyList<(string Name, ViewValue Value, string SqlType)> Columns, string? WhyNot);

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

        var views = new List<MappedView>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var view = MapView(declared[i], i, flow, dimensions, views, source);
            if (views.FirstOrDefault(v => string.Equals(v.Spec.Name, view.Spec.Name, StringComparison.OrdinalIgnoreCase)) is { } twice)
            {
                throw new FlowValidationException(
                    string.Create(CultureInfo.InvariantCulture, $"{source}: views[{i}] is named '{view.Spec.Name}', as views '{twice.Spec.Name}' is, ignoring case; each view is a view of its own name in the database."));
            }

            views.Add(view);
        }

        return views.Select(v => v.Spec).ToList();
    }

    private static MappedView MapView(
        DimensionViewYaml? v, int index, string flow, IReadOnlyList<DimensionSpec> dimensions, IReadOnlyList<MappedView> earlier, string source)
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
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ViewScope.FromAlias };
        var joinedViews = new List<MappedView>();
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
            var join = declaredJoins[i] ?? throw new FlowValidationException($"{source}: {place} is empty; a join is {{ on: <column>, to: <dimension>, as: <alias> }}, or {{ on: <column>, view: <view>, as: <alias> }}.");
            var on = FlowMapper.Require(join.On, place + ".on", source).Trim();
            if ((join.To is null) == (join.View is null))
            {
                throw new FlowValidationException(
                    $"{source}: {place} names {(join.To is null ? "neither to nor view" : "both to and view")}; a join reads a dimension of the flow ({{ on: <column>, to: <dimension> }}) or a view declared before this one ({{ on: <column>, view: <view> }}).");
            }

            MappedView? toView = null;
            DimensionSpec? to = null;
            if (join.View is not null)
            {
                var joinedView = join.View.Trim();
                toView = earlier.FirstOrDefault(e => string.Equals(e.Spec.Name, joinedView, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FlowValidationException(
                        $"{source}: {place}.view names '{joinedView}', which is no view declared before this one in the flow{(earlier.Count == 0 ? " (none is)" : $" ({string.Join(", ", earlier.Select(e => e.Spec.Name))})")}; a view joins views declared before it, so a build writes each before the views that read it.");
                if (toView.JoinColumn is null)
                {
                    throw new FlowValidationException($"{source}: {place} joins view {toView.Spec.Name}, which a join cannot meet one row of: {toView.WhyNot}.");
                }
            }
            else
            {
                var toName = join.To!.Trim();
                to = Dimension(dimensions, toName)
                    ?? throw new FlowValidationException($"{source}: {place}.to names '{toName}', which is not a dimension of the flow; its dimensions are {Listed(dimensions)}. A view joins the dimensions of its own flow, so one run settles every table it reads.");
            }

            var joinedName = to?.Name ?? toView!.Spec.Name;
            var alias = join.As is null ? joinedName : join.As.Trim();
            if (!DimensionViewSpec.IsName(alias))
            {
                throw new FlowValidationException(
                    $"{source}: {place}.as '{alias}' is not an alias: a letter, then letters, digits and underscores, at most {DimensionViewSpec.MaxNameLength}.{(join.As is null ? $" The alias is the {(to is null ? "view" : "dimension")}'s name unless the join gives one; give it one: as: <alias>." : string.Empty)}");
            }

            if (aliases.Contains(alias))
            {
                throw new FlowValidationException(
                    $"{source}: {place} reads {joinedName} as {alias}, an alias an earlier join has, ignoring case; give each join of the same {(to is null ? "view" : "dimension")} its own: as: <alias>.");
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

            if (!read.TryGetValue(onAlias, out var holder))
            {
                throw new FlowValidationException(
                    $"{source}: {place}.on '{on}' is a column of {onAlias}, a view this one joins; a join is on a column of the from dimension or of a dimension it joins, which hold the keys a join compares.");
            }

            var (key, why) = DimensionViews.KeyOf(holder, onSource.Column);
            if (key is null)
            {
                throw new FlowValidationException($"{source}: {place}.on '{on}' holds no key: {why}.");
            }

            var meets = DimensionViews.Meets(key, to is null ? toView!.Key! : DimensionViews.KeyOf(to));
            if (meets is not null)
            {
                throw new FlowValidationException($"{source}: {place} joins '{on}' to {joinedName}, and {meets}.");
            }

            if (to is not null && DimensionViews.RowsPerKey(to))
            {
                throw new FlowValidationException(
                    $"{source}: {place} joins {to.Name}, whose table holds a row per {(to.Elements is not null ? "element" : "value it collects")} of each key, so a join to it would repeat the view's rows. Make it the view's from dimension, join a dimension of the same key without {(to.Elements is not null ? "elements" : "a collected attribute")}, or join a view that groups its rows by the key (view: <view>).");
            }

            var sqlAlias = string.Create(CultureInfo.InvariantCulture, $"j{i + 1}");
            aliases.Add(alias);
            if (to is not null)
            {
                var table = DimensionTables.NameOf(to.Name);
                joins.Add(new DimensionViewJoin(on, to.Name, alias));
                joinSql.Add(new DimensionViews.JoinSql(table, sqlAlias, to.KeyColumn, onSource.Sql));
                joinDefinitions.Add(new DimensionViewJoinDefinition(alias, to.Name, on, table, sqlAlias, onSource.Sql));
                joinedTables.Add((alias, sqlAlias, to));
                scope.Add(alias, to.Name, DimensionViews.ColumnsOf(to, alias, sqlAlias));
                read[alias] = to;
            }
            else
            {
                var target = toView!;
                joins.Add(new DimensionViewJoin(on, target.Spec.Name, alias, View: true));
                joinSql.Add(new DimensionViews.JoinSql(target.Spec.ViewName, sqlAlias, target.JoinColumn!, onSource.Sql, View: true));
                joinDefinitions.Add(new DimensionViewJoinDefinition(alias, target.Spec.Name, on, target.Spec.ViewName, sqlAlias, onSource.Sql, View: true));
                scope.Add(alias, $"view {target.Spec.Name}", ViewColumns(target, alias, sqlAlias));
                if (!joinedViews.Contains(target))
                {
                    joinedViews.Add(target);
                }
            }
        }

        // The columns a grouped view groups by: each a column of the view's tables, named as an expression names one.
        var groupedBy = GroupBy(v.GroupBy, scope, where, source);
        if (groupedBy.Count > 0 && v.Columns is not { Count: > 0 })
        {
            throw new FlowValidationException(
                $"{source}: {where}: groupBy groups the view's rows, and a grouped view holds the columns it lists: the columns it groups by and aggregates of the rest. List them under columns.");
        }

        if (joinedViews.Count > 0 && v.Columns is not { Count: > 0 })
        {
            throw new FlowValidationException(
                $"{source}: {where}: the view joins view {joinedViews[0].Spec.Name}, and a view that joins a view holds the columns it lists; list them under columns.");
        }

        var (columns, specs, applies, values) = v.Columns is { Count: > 0 } listed
            ? ListedColumns(listed, scope, groupedBy.Count == 0 ? null : groupedBy, where, source)
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
        var viewName = DimensionViewSpec.Prefix + name;
        var tables = new List<string> { fromTable };
        tables.AddRange(joinDefinitions.Where(j => !j.View).Select(j => j.Table).Where(t => !tables.Contains(t, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase));
        var viewsRead = joinedViews.Select(j => j.Spec.ViewName).ToList();
        DimensionViewDefinition definition;
        if (groupedBy.Count == 0)
        {
            var fromSql = DimensionViews.FromSql(fromTable, joinSql, applies);
            var all = new List<DimensionViewColumnDefinition>
            {
                new("partition", "nvarchar(256)", $"[{DimensionViews.FromSqlAlias}].[partition]", null, null, "The partition the row was read in.", null),
                new("id", "bigint", $"[{DimensionViews.FromSqlAlias}].[id]", null, null, $"The number of the row of {fromTable} the view's row stands for: the view's key.", null),
            };
            all.AddRange(columns);
            var createSql = DimensionViews.CreateSql(flow, viewName, all, fromSql, whereSql);
            definition = new DimensionViewDefinition(
                viewName, tables, all, joinDefinitions, fromSql, createSql, DimensionViews.HashOf(createSql, joinedViews.Select(j => j.Spec.Definition.Hash)), whereSql,
                viewsRead.Count == 0 ? null : viewsRead);
        }
        else
        {
            // The rows are grouped in a derived table, under the alias the from table has, so the check reads a grouped view
            // the way it reads any other: each column computed there once, then converted over it.
            var rowsSql = DimensionViews.FromSql(fromTable, joinSql, []);
            var all = new List<DimensionViewColumnDefinition>
            {
                new("partition", "nvarchar(256)", $"[{DimensionViews.FromSqlAlias}].[partition]", null, null, "The partition the row was read in.", null),
                new("id", "bigint", $"[{DimensionViews.FromSqlAlias}].[id]", null, null, $"The least number of the rows of {fromTable} the view's row groups: the view's key.", null),
            };
            all.AddRange(columns);
            var fromSql = DimensionViews.GroupedFromSql(rowsSql, whereSql, groupedBy.Select(g => g.Sql).ToList(), columns);
            var createSql = DimensionViews.CreateSql(flow, viewName, all, fromSql, null);
            definition = new DimensionViewDefinition(
                viewName, tables, all, joinDefinitions, fromSql, createSql, DimensionViews.HashOf(createSql, joinedViews.Select(j => j.Spec.Definition.Hash)), null,
                viewsRead.Count == 0 ? null : viewsRead, rowsSql, whereSql);
        }

        var spec = new DimensionViewSpec
        {
            Name = name,
            Description = description,
            From = from.Name,
            Joins = joins,
            Columns = specs,
            Where = condition,
            GroupBy = groupedBy.Select(g => g.Named).ToList(),
            Definition = definition,
        };

        return Joinable(spec, groupedBy, read, columns, values);
    }

    /// <summary>
    /// <paramref name="spec"/> as a later view may join it: a view that groups by one column holding a key, and lists that
    /// column bare, meets one row of a key, by that column; any other is mapped with why it cannot be joined.
    /// </summary>
    private static MappedView Joinable(
        DimensionViewSpec spec, IReadOnlyList<ViewColumnSource> groupedBy, Dictionary<string, DimensionSpec> read,
        IReadOnlyList<DimensionViewColumnDefinition> columns, IReadOnlyList<ViewValue> values)
    {
        var typed = new List<(string, ViewValue, string)>(columns.Count + 2) { ("partition", ViewValue.Text, "nvarchar(256)"), ("id", ViewValue.Number, "bigint") };
        typed.AddRange(columns.Select((c, i) => (c.Name, values[i], c.Type)));
        if (groupedBy.Count != 1)
        {
            return new MappedView(spec, null, null, typed, groupedBy.Count == 0
                ? "it groups no rows, so it holds a row per row of its from dimension, and a key can have many; give it groupBy: <key column>"
                : $"it groups its rows by {groupedBy.Count} columns, and a join compares one key; join a view grouped by the key alone");
        }

        var group = groupedBy[0];
        if (!read.TryGetValue(group.Alias, out var holder))
        {
            return new MappedView(spec, null, null, typed, $"it groups by {group.Named}, a column of a view it joins, which holds no key a join compares");
        }

        var (key, why) = DimensionViews.KeyOf(holder, group.Column);
        if (key is null)
        {
            return new MappedView(spec, null, null, typed, $"it groups by {group.Named}, which holds no key: {why}");
        }

        // The column a join compares: the one the view lists as the group column itself, bare and not converted.
        var listed = columns.FirstOrDefault(c => c.DataType is null && string.Equals(c.GroupSql, group.Sql, StringComparison.Ordinal));
        return listed is null
            ? new MappedView(spec, null, null, typed, $"it groups by {group.Named} and does not list it as a column of its own, bare and not converted, which a join compares; list it (columns: {{ {group.Column}: {group.Named} }})")
            : new MappedView(spec, listed.Name, key, typed, null);
    }

    /// <summary>The columns of a view a join reads, under the join's alias and the SQL alias of its table.</summary>
    private static IEnumerable<ViewColumnSource> ViewColumns(MappedView view, string alias, string sqlAlias)
        => view.Columns.Select(c => new ViewColumnSource(alias, sqlAlias, c.Name, c.Value, c.SqlType));

    /// <summary>
    /// The columns a view groups its rows by (<c>groupBy</c>): one column or a list of them, each named as an expression
    /// names one (bare for the from dimension's, <c>alias.column</c> for a join's), each once, at most
    /// <see cref="DimensionViewSpec.MaxGroupBy"/>; none when the view groups nothing.
    /// </summary>
    private static IReadOnlyList<ViewColumnSource> GroupBy(object? declared, ViewScope scope, string where, string source)
    {
        List<string> written = declared switch
        {
            null => [],
            string one => [one],
            IEnumerable<object?> many => many.Select((c, i) => c as string ?? throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: groupBy[{i}] is not a column; each is a column of the view's tables, such as WellLogID or Log.WellboreID."))).ToList(),
            _ => throw new FlowValidationException($"{source}: {where}: groupBy is a column (groupBy: WellLogID) or a list of columns (groupBy: [WellLogID, Log.Source]); it is neither."),
        };
        if (declared is not null && written.Count == 0)
        {
            throw new FlowValidationException($"{source}: {where}: groupBy lists no column; give the column the view groups its rows by, or leave groupBy out.");
        }

        if (written.Count > DimensionViewSpec.MaxGroupBy)
        {
            throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture,
                $"{source}: {where}: groupBy lists {written.Count} columns; a view groups by at most {DimensionViewSpec.MaxGroupBy}."));
        }

        var columns = new List<ViewColumnSource>(written.Count);
        for (var i = 0; i < written.Count; i++)
        {
            var text = written[i].Trim();
            var parts = text.Split('.');
            if (parts.Length > 2 || parts.Any(p => p.Trim().Length == 0))
            {
                throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture,
                    $"{source}: {where}: groupBy[{i}] '{text}' is not a column: it is a column of the from dimension, bare (WellLogID), or of a join, by its alias (Log.WellboreID)."));
            }

            ViewColumnSource column;
            try
            {
                column = parts.Length == 1 ? scope.Resolve(ViewScope.FromAlias, parts[0].Trim()) : scope.Resolve(parts[0].Trim(), parts[1].Trim());
            }
            catch (ViewExpressionException ex)
            {
                throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: groupBy[{i}] {ex.Message}."), ex);
            }

            if (columns.Any(c => string.Equals(c.Sql, column.Sql, StringComparison.Ordinal)))
            {
                throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: groupBy[{i}] names {column.Named} twice."));
            }

            columns.Add(column);
        }

        return columns;
    }

    /// <summary>
    /// The columns the document lists, each an expression compiled over the view's tables and converted to its data type,
    /// with the kind of value each gives. For a grouped view each is computed in the derived table that groups the rows
    /// (<see cref="DimensionViews.GroupedFromSql"/>), as <c>[b].[name]</c> there, and converted over it.
    /// </summary>
    private static (List<DimensionViewColumnDefinition> Columns, List<DimensionViewColumnSpec> Specs, List<string> Applies, List<ViewValue> Values) ListedColumns(
        Dictionary<string, object?> listed, ViewScope scope, IReadOnlyList<ViewColumnSource>? groupedBy, string where, string source)
    {
        if (listed.Count > DimensionViewSpec.MaxColumns)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: columns lists {listed.Count} columns; a view holds at most {DimensionViewSpec.MaxColumns} beside its partition and id."));
        }

        var columns = new List<DimensionViewColumnDefinition>(listed.Count);
        var specs = new List<DimensionViewColumnSpec>(listed.Count);
        var applies = new List<string>();
        var values = new List<ViewValue>(listed.Count);

        // The order the grouped view's ordered aggregates read a group in, and the first column that named it.
        (string Order, string Column)? ordered = null;
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
                compiled = DimensionViewExpressions.Compile(expression, scope, groupedBy);
            }
            catch (ViewExpressionException ex)
            {
                throw new FlowValidationException($"{source}: {at}: the expression {Quoted(expression)} {ex.Message}.", ex);
            }

            if (groupedBy is not null && compiled.Orders is [var order])
            {
                if (ordered is { } first && !string.Equals(first.Order, order, StringComparison.Ordinal))
                {
                    throw new FlowValidationException(
                        $"{source}: {at}: the expression {Quoted(expression)} orders the rows of a group otherwise than columns.{first.Column} does ({order}, where it orders them {first.Order}), and SQL Server reads a group in one order: order every STRING_AGG of a grouped view alike, or in none.");
                }

                ordered ??= (order, name);
            }

            if (groupedBy is not null)
            {
                // Computed in the derived table that groups the rows, then read (and converted) over it by its name there.
                var grouped = $"[{DimensionViews.FromSqlAlias}].{DimensionViewExpressions.Quoted(name)}";
                if (dataType is null)
                {
                    columns.Add(new DimensionViewColumnDefinition(
                        name, compiled.Column?.SqlType ?? DimensionViewExpressions.TypeWord(compiled.Value), grouped, expression, null, description, null, compiled.Sql));
                    values.Add(compiled.Value);
                }
                else
                {
                    try
                    {
                        var type = DimensionConversions.Parse(dataType);
                        columns.Add(new DimensionViewColumnDefinition(
                            name, type.Render(), DimensionConversions.Convert(grouped, compiled.Value, type), expression, type.Render(), description, grouped, compiled.Sql));
                        values.Add(DimensionConversions.ValueOf(type));
                    }
                    catch (ViewExpressionException ex)
                    {
                        throw new FlowValidationException($"{source}: {at}: dataType {ex.Message}.", ex);
                    }
                }

                specs.Add(new DimensionViewColumnSpec(name, expression, dataType, description));
                continue;
            }

            if (dataType is null)
            {
                columns.Add(new DimensionViewColumnDefinition(
                    name, compiled.Column?.SqlType ?? DimensionViewExpressions.TypeWord(compiled.Value), compiled.Sql, expression, null, description, null));
                values.Add(compiled.Value);
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
                    values.Add(DimensionConversions.ValueOf(type));
                }
                catch (ViewExpressionException ex)
                {
                    throw new FlowValidationException($"{source}: {at}: dataType {ex.Message}.", ex);
                }
            }

            specs.Add(new DimensionViewColumnSpec(name, expression, dataType, description));
        }

        return (columns, specs, applies, values);
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
    private static (List<DimensionViewColumnDefinition> Columns, List<DimensionViewColumnSpec> Specs, List<string> Applies, List<ViewValue> Values) DefaultColumns(
        DimensionSpec from, IReadOnlyList<(string Alias, string SqlAlias, DimensionSpec Dimension)> joins, string where, string source)
    {
        var columns = new List<DimensionViewColumnDefinition>();
        var values = new List<ViewValue>();
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
            values.Add(column.Value);
        }

        return columns.Count > DimensionViewSpec.MaxColumns
            ? throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: the view would hold {columns.Count} columns, and a view holds at most {DimensionViewSpec.MaxColumns} beside its partition and id; list the columns it holds."))
            : (columns, [], [], values);
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
