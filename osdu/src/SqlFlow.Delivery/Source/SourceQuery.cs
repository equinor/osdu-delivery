using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Source;

/// <summary>One column of the scope predicate as a script gives it: what the column holds, and the values the read is given.</summary>
/// <param name="Column">The record table's column.</param>
/// <param name="Parameter">The run parameter whose value fills it.</param>
/// <param name="SqlType">The type the column holds, which the script declares the value as.</param>
/// <param name="Values">The values the read is given, one or several.</param>
public sealed record SourceScopeValues(string Column, string Parameter, string SqlType, IReadOnlyList<string> Values);

/// <summary>A flow's source as a query a person runs needs it: the layout a read pages with, and what the tables hold. Reading it reads no row.</summary>
/// <param name="Layout">The source as a read sees it.</param>
/// <param name="Columns">The columns each table holds, by the name a mapping reads it under (<see cref="SourceDatasets.Record"/> or a child dataset's).</param>
/// <param name="Scope">The scope predicate's values, in the order of <see cref="IngestionLayout.Scope"/>.</param>
/// <param name="Origin">The record table's columns saying which file and row a record came from.</param>
/// <param name="Types">
/// The SQL type of each column, by table as <paramref name="Columns"/> names them; null when the source does not say, and a
/// column it does not list, which a query then compares as text.
/// </param>
public sealed record SourceDescription(
    IngestionLayout Layout,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Columns,
    IReadOnlyList<SourceScopeValues> Scope,
    IReadOnlyList<string> Origin,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Types = null)
{
    /// <summary>The SQL type of <paramref name="column"/> of the table <paramref name="dataset"/> names, or null when the source does not say.</summary>
    public string? TypeOf(string dataset, string column)
        => Types?.GetValueOrDefault(dataset) is { } types && types.TryGetValue(column, out var type) ? type : null;
}

/// <summary>A source column an attribute reads, as the mapping names it, and the table holding it.</summary>
public sealed record SourceQueryRead(string Column, string Table);

/// <summary>
/// The script a person runs in SSMS to see what an attribute of a mapping reads, as the source tables hold it now
/// (osdu/docs/reference/concepts/gui-build.md, Mappings).
/// </summary>
/// <param name="Flow">The flow whose rows it reads.</param>
/// <param name="Interface">The interface of the flow.</param>
/// <param name="Mapping">The mapping the attribute is of.</param>
/// <param name="Target">The attribute, as a template path; null for every entry of the mapping.</param>
/// <param name="Entries">How many entries of the mapping reach the attribute. None means nothing of the mapping fills it.</param>
/// <param name="Reads">The source columns those entries read, in the tables holding them.</param>
/// <param name="Missing">The columns they read that the tables do not hold, as the mapping names them, which the script leaves out.</param>
/// <param name="Sql">The script; null when the entries read no source column.</param>
/// <param name="Keys">How many records the script is narrowed to; 0 for every row of the scope.</param>
public sealed record SourceQueryAnswer(
    string Flow,
    string? Interface,
    string Mapping,
    string? Target,
    int Entries,
    IReadOnlyList<SourceQueryRead> Reads,
    IReadOnlyList<string> Missing,
    string? Sql,
    int Keys);

/// <summary>
/// Writes what an attribute of a mapping reads as T-SQL over the flow's own tables. The statements are built from the layout and
/// the predicates a read sends (<see cref="IngestionSql.Inspection"/>), so they name the rows a delivery reads and nothing that
/// merely looks like them, and they show the source as it is, before the mapping's lookups and expressions. A record's child
/// rows are joined so a record that has none still shows, and each child dataset beyond one has a statement of its own, since
/// two joined together would multiply each other's rows. Nothing here reads a row or runs a statement.
/// </summary>
public static class SourceQuery
{
    /// <summary>The script for <paramref name="target"/>, or every entry of the mapping when it is null.</summary>
    /// <param name="source">The flow's source.</param>
    /// <param name="mapping">The mapping the flow renders with.</param>
    /// <param name="flow">The flow's label.</param>
    /// <param name="flowInterface">The flow's interface.</param>
    /// <param name="target">The attribute.</param>
    /// <param name="maxRows">The most rows a statement returns; 0 for all of them.</param>
    /// <param name="keys">The records to narrow to; none for every row of the scope.</param>
    public static SourceQueryAnswer For(
        SourceDescription source, MappingDefinition mapping, string flow, string? flowInterface, TemplatePath? target, long maxRows, IReadOnlyList<KeyTuple> keys)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(flow);
        ArgumentNullException.ThrowIfNull(keys);

        var selection = target is null ? EntrySelection.All : EntrySelection.Of([target]);
        var entries = mapping.Entries.Where(entry => selection.Includes(entry.Target.Text)).ToList();
        var layout = source.Layout;
        var datasets = layout.Datasets.ToDictionary(dataset => dataset.Name, StringComparer.OrdinalIgnoreCase);

        var record = new List<string>();
        var children = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var reads = new List<SourceQueryRead>();
        var missing = new List<string>();

        // A repeated item is written from the rows of its child dataset, whether or not the attribute reads a column of them.
        foreach (var child in entries.Where(entry => entry.IsRepeater).Select(entry => entry.Source?.Child).OfType<string>())
        {
            if (datasets.ContainsKey(child))
            {
                children.TryAdd(child, []);
            }
        }

        foreach (var column in entries.SelectMany(entry => entry.Columns).Distinct())
        {
            var dataset = column.Child ?? SourceDatasets.Record;
            var named = source.Columns.GetValueOrDefault(dataset)?.FirstOrDefault(held => held.Equals(column.Column, StringComparison.OrdinalIgnoreCase));
            if (named is null)
            {
                missing.Add(column.ToString());
            }
            else if (column.Child is null)
            {
                record.Add(named);
                reads.Add(new SourceQueryRead(column.ToString(), layout.Record.ToString()));
            }
            else
            {
                if (!children.TryGetValue(column.Child, out var read))
                {
                    read = [];
                    children[column.Child] = read;
                }

                read.Add(named);
                reads.Add(new SourceQueryRead(column.ToString(), datasets[column.Child].Object.ToString()));
            }
        }

        var statements = Statements(layout, record, children, datasets);
        var what = target?.Text ?? "Every attribute";
        return new SourceQueryAnswer(
            flow,
            flowInterface,
            mapping.Reference,
            target?.Text,
            entries.Count,
            reads,
            missing,
            statements.Count == 0 ? null : Script($"{what} of {mapping.Reference}", flow, source, statements, missing, maxRows, keys),
            keys.Count);
    }

    /// <summary>What each statement reads: one, unless several child datasets would multiply each other's rows.</summary>
    private static List<InspectionRead> Statements(
        IngestionLayout layout, List<string> record, SortedDictionary<string, List<string>> children, Dictionary<string, IngestionDataset> datasets)
    {
        if (children.Count == 0)
        {
            return record.Count == 0 ? [] : [new InspectionRead(record, null, [])];
        }

        if (children.Count == 1)
        {
            var (name, columns) = children.First();
            return [new InspectionRead(record, datasets[name], columns)];
        }

        var statements = new List<InspectionRead>(children.Count + 1);
        if (record.Count > 0)
        {
            statements.Add(new InspectionRead(record, null, []));
        }

        statements.AddRange(children.Select(child => new InspectionRead([], datasets[child.Key], child.Value)));
        return statements;
    }

    private static string Script(
        string what, string flow, SourceDescription source, List<InspectionRead> statements, List<string> missing, long maxRows, IReadOnlyList<KeyTuple> keys)
    {
        var script = new StringBuilder();
        Comment(script, $"{what}, in the rows {flow} reads, before the mapping renders them.");
        if (missing.Count > 0)
        {
            Comment(script, $"The mapping reads columns the tables do not hold, left out here: {string.Join(", ", missing)}");
        }

        DeclareScope(script, source);
        if (keys.Count > 0)
        {
            script.Append("DECLARE ").Append(IngestionSql.KeysParameter).Append(" nvarchar(max) = ")
                .Append(Text("[" + string.Join(",", keys.Select(key => key.Json)) + "]")).AppendLine(";");
        }

        foreach (var statement in statements)
        {
            script.AppendLine();
            script.AppendLine(IngestionSql.Inspection(source.Layout, statement, source.Origin, keys.Count > 0 ? 0 : maxRows, keys.Count > 0));
        }

        return script.ToString();
    }

    /// <summary>
    /// The script a person runs in SSMS to read what a look at a table read (<see cref="IIngestionSource.ReadTableAsync"/>): the
    /// same rows, in the same order, each value whole where the look cuts a long one. Each lookup table is joined as a delivery
    /// joins it, so a run of this beside the cache's script (<see cref="Catalog.CacheProofSql.ItemsScript"/>) cross-checks what
    /// the join gives a row against what the cache holds. The values the read was given are declared as the parameters it
    /// bound them to: the scope's, the rows to list and, for a search, what the search reads as.
    /// </summary>
    /// <param name="flow">The flow's label.</param>
    /// <param name="table">The table read, as the flow names it.</param>
    /// <param name="child">True when the table is a child dataset's, read for the records in scope.</param>
    /// <param name="lookups">The lookup tables joined into the read.</param>
    /// <param name="scope">The scope's values, in the order of <see cref="IngestionLayout.Scope"/>.</param>
    /// <param name="rows">The most rows the script lists.</param>
    /// <param name="search">The search the rows were narrowed by, with its text; null for none.</param>
    /// <param name="statement">The statement (<see cref="IngestionSql.TableRows"/>, without its count).</param>
    internal static string TableRows(
        string flow, string table, bool child, IReadOnlyList<IngestionLookup> lookups, IReadOnlyList<SourceScopeValues> scope, int rows,
        (string Text, TableSearchTerm Term)? search, string statement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(lookups);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);

        var script = new StringBuilder();
        Comment(script, child
            ? $"The first {rows.ToString(CultureInfo.InvariantCulture)} rows of {table} that {flow} reads for the records in this scope, as the overview lists them, each value whole."
            : $"The first {rows.ToString(CultureInfo.InvariantCulture)} rows of {table} that {flow} reads in this scope, as the overview lists them, each value whole.");
        Comment(script, "Runs on the flow's source database, as the tables hold them now.");
        if (lookups.Count > 0)
        {
            Comment(script, $"Joined as a delivery joins them, by a LEFT OUTER JOIN on each key, a row marked deleted left out: {string.Join(", ", lookups.Select(l => $"{l.Name} ({l.Table})"))}.");
        }

        DeclareScope(script, scope);
        Comment(script, "The most rows to list.");
        script.Append("DECLARE ").Append(IngestionSql.PageParameter).Append(" int = ").Append(rows.ToString(CultureInfo.InvariantCulture)).AppendLine(";");
        if (search is { } searched)
        {
            var term = searched.Term;
            Comment(script, $"The search: {searched.Text}, found inside any column's text, ignoring case, and as a number or a moment it reads as.");
            Declare(script, statement, IngestionSql.SearchParameter, "nvarchar(4000)", Text(term.Pattern));
            if (term.Float is { } wide)
            {
                Declare(script, statement, IngestionSql.SearchFloatParameter, "float", wide.ToString("R", CultureInfo.InvariantCulture));
            }

            if (term.Real is { } narrow)
            {
                Declare(script, statement, IngestionSql.SearchRealParameter, "real", narrow.ToString("R", CultureInfo.InvariantCulture));
            }

            if (term.Moment is { } moment)
            {
                Declare(script, statement, IngestionSql.SearchMomentParameter, "datetimeoffset(7)", Text(moment.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture)));
            }
        }

        script.AppendLine();
        script.AppendLine(statement);
        return script.ToString();
    }

    /// <summary>Declares <paramref name="name"/> when <paramref name="statement"/> reads it: a search binds a form no column compares with, which the statement leaves out.</summary>
    private static void Declare(StringBuilder script, string statement, string name, string type, string literal)
    {
        if (Regex.IsMatch(statement, Regex.Escape(name) + "(?![A-Za-z0-9_])", RegexOptions.CultureInvariant))
        {
            script.Append("DECLARE ").Append(name).Append(' ').Append(type).Append(" = ").Append(literal).AppendLine(";");
        }
    }

    /// <summary>Declares the scope's values as the parameters a read binds them to, each under a comment naming its parameter.</summary>
    internal static void DeclareScope(StringBuilder script, SourceDescription source)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(source);
        DeclareScope(script, source.Scope);
    }

    /// <summary>Declares <paramref name="scopes"/> as the parameters a read binds them to, each under a comment naming its parameter.</summary>
    private static void DeclareScope(StringBuilder script, IReadOnlyList<SourceScopeValues> scopes)
    {
        for (var i = 0; i < scopes.Count; i++)
        {
            var scope = scopes[i];

            // Over its statement, as formatting the script would leave a comment beside it over the next one.
            Comment(script, $"The scope: {scope.Parameter}");
            for (var value = 0; value < scope.Values.Count; value++)
            {
                var name = scope.Values.Count == 1 ? IngestionSql.ScopeParameter(i) : IngestionSql.ScopeParameter(i, value);
                script.Append("DECLARE ").Append(name).Append(' ').Append(scope.SqlType).Append(" = ").Append(Text(scope.Values[value])).AppendLine(";");
            }
        }
    }

    private static void Comment(StringBuilder script, string text) => script.Append("-- ").AppendLine(OneLine(text));

    /// <summary>Text that stays inside the comment it is written in, whatever a name in a flow holds.</summary>
    private static string OneLine(string text) => text.ReplaceLineEndings(" ");

    /// <summary>A value as a T-SQL literal: Unicode, with every quote doubled, so no value can end the string it is in.</summary>
    private static string Text(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
