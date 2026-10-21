using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SqlFlow.Delivery.Data;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The table pages (<c>osdu/docs/reference/tables</c>, one page of type <c>table</c> per table of the <c>osdu</c> schema)
/// document the model the migrations build, as the census files document the keys the loaders accept: every table of the
/// model has its page and every page names a table of the model; a page's Columns section lists the table's columns with
/// their SQL type, identity and nullability, and its Keys and indexes section the primary key and every index with its
/// columns in order (descending ones marked), its kind, its filter and its included columns. A column's database default
/// and collation, and the table's check constraints, are named on the page, and the schema's page
/// (<c>osdu/docs/reference/concepts/database.md</c>) links every table page. What a column holds and what an index serves
/// is prose the test does not read. A failure prints the rows the page should carry.
/// </summary>
public sealed partial class DatabaseTablePagesTests
{
    private const string Placeholder = "Server=127.0.0.1,1;Database=unused;TrustServerCertificate=True;Connect Timeout=1;ConnectRetryCount=0";

    private static readonly Lazy<IReadOnlyList<ModelTable>> Model = new(ReadModel);

    private static string TablesFolder => RepositoryRoot.Combine("osdu", "docs", "reference", "tables");

    public static TheoryData<string> Tables()
    {
        var data = new TheoryData<string>();
        foreach (var table in Model.Value)
        {
            data.Add(table.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void A_table_page_documents_the_tables_columns_keys_and_indexes(string tableName)
    {
        var table = Model.Value.Single(t => t.Name == tableName);
        var path = Path.Combine(TablesFolder, Slug(table.Name) + ".md");
        Assert.True(File.Exists(path), $"osdu.{table.Name} has no page: write {Relative(path)}.\n\n{Expected(table)}");

        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var problems = new List<string>();

        var front = Frontmatter(text);
        Expect(problems, front.GetValueOrDefault("id") == $"delivery-table-{Slug(table.Name)}", $"its id is delivery-table-{Slug(table.Name)}");
        Expect(problems, front.GetValueOrDefault("type") == "table", "its type is table");
        Expect(problems, front.GetValueOrDefault("title")?.StartsWith($"osdu.{table.Name}: ", StringComparison.Ordinal) == true,
            $"its title starts \"osdu.{table.Name}: \"");

        var columns = Rows(text, "Columns", 4, problems);
        var documented = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var row in columns)
        {
            if (!documented.TryAdd(Unquote(row[0]), row))
            {
                problems.Add($"the column {row[0]} is listed twice");
            }
        }

        foreach (var column in table.Columns)
        {
            if (!documented.Remove(column.Name, out var row))
            {
                problems.Add($"the column `{column.Name}` is missing: {ColumnRow(column)}");
                continue;
            }

            Expect(problems, row[1] == TypeCell(column), $"`{column.Name}` has the type {TypeCell(column)}, not {row[1]}");
            Expect(problems, row[2] == (column.Nullable ? "yes" : "no"), $"`{column.Name}` is {(column.Nullable ? "nullable (yes)" : "not nullable (no)")}, not {row[2]}");
            Expect(problems, !string.IsNullOrWhiteSpace(row[3]), $"`{column.Name}` says what it holds");
            if (column.Default is { } defaultValue)
            {
                Expect(problems, row[3].Contains($"Default `{defaultValue}`", StringComparison.Ordinal), $"`{column.Name}` names its default: Default `{defaultValue}`");
            }

            if (column.Collation is { } collation)
            {
                Expect(problems, row[3].Contains($"Collation `{collation}`", StringComparison.Ordinal), $"`{column.Name}` names its collation: Collation `{collation}`");
            }
        }

        foreach (var extra in documented.Keys)
        {
            problems.Add($"the column `{extra}` is not a column of osdu.{table.Name}");
        }

        var indexes = Rows(text, "Keys and indexes", 6, problems);
        var listed = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var row in indexes)
        {
            if (!listed.TryAdd(Unquote(row[0]), row))
            {
                problems.Add($"the index {row[0]} is listed twice");
            }
        }

        foreach (var index in table.Indexes)
        {
            if (!listed.Remove(index.Name, out var row))
            {
                problems.Add($"the index `{index.Name}` is missing: {IndexRow(index)}");
                continue;
            }

            Expect(problems, row[1] == index.Columns, $"`{index.Name}` covers {index.Columns}, not {row[1]}");
            Expect(problems, row[2] == index.Kind, $"`{index.Name}` is a {index.Kind}, not {row[2]}");
            Expect(problems, row[3] == index.Filter, $"`{index.Name}` has the filter {index.Filter}, not {row[3]}");
            Expect(problems, row[4] == index.Includes, $"`{index.Name}` includes {index.Includes}, not {row[4]}");
            Expect(problems, !string.IsNullOrWhiteSpace(row[5]), $"`{index.Name}` says what it serves");
        }

        foreach (var extra in listed.Keys)
        {
            problems.Add($"the index `{extra}` is not an index of osdu.{table.Name}");
        }

        foreach (var check in table.Checks)
        {
            Expect(problems, text.Contains($"`{check.Name}`", StringComparison.Ordinal) && text.Contains($"`{check.Sql}`", StringComparison.Ordinal),
                $"the page names the check constraint `{check.Name}` and its condition `{check.Sql}`");
        }

        Assert.True(problems.Count == 0,
            $"{Relative(path)} does not match osdu.{table.Name}:\n- {string.Join("\n- ", problems)}\n\n{Expected(table)}");
    }

    [Fact]
    public void Every_table_page_names_a_table_of_the_model()
    {
        Assert.True(Directory.Exists(TablesFolder), $"{Relative(TablesFolder)} is missing.");
        var tables = Model.Value.Select(t => Slug(t.Name)).ToHashSet(StringComparer.Ordinal);
        var strays = Directory.EnumerateFiles(TablesFolder, "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(slug => !tables.Contains(slug!))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(strays.Count == 0, "Pages that name no table of the osdu schema: " + string.Join(", ", strays));
    }

    [Fact]
    public void Every_table_of_the_model_is_in_the_osdu_schema_and_the_schema_page_links_its_page()
    {
        Assert.NotEmpty(Model.Value);
        Assert.All(Model.Value, t => Assert.Equal(DeliveryModel.SchemaName, t.Schema));

        var schemaPage = RepositoryRoot.Combine("osdu", "docs", "reference", "concepts", "database.md");
        Assert.True(File.Exists(schemaPage), $"{Relative(schemaPage)} is missing.");
        var text = File.ReadAllText(schemaPage);
        var unlinked = Model.Value.Where(t => !text.Contains($"](../tables/{Slug(t.Name)}.md)", StringComparison.Ordinal)).Select(t => t.Name).ToList();
        Assert.True(unlinked.Count == 0, $"{Relative(schemaPage)} does not link the pages of: " + string.Join(", ", unlinked));
    }

    /// <summary>A table's page name: <c>RecordEvent</c> is <c>record-event</c>.</summary>
    public static string Slug(string table) => UpperCase().Replace(table, "-$1").ToLowerInvariant();

    private static IReadOnlyList<ModelTable> ReadModel()
    {
        using var context = new OsduDbContext(OsduDbContext.SqlServerOptions(Placeholder));
        var model = context.GetService<IDesignTimeModel>().Model;
        var tables = new List<ModelTable>();
        foreach (var entity in model.GetEntityTypes())
        {
            var name = entity.GetTableName() ?? throw new InvalidOperationException($"{entity.DisplayName()} maps to no table.");
            var store = StoreObjectIdentifier.Table(name, entity.GetSchema());

            var columns = entity.GetProperties()
                .Select(p => new ModelColumn(
                    p.GetColumnName(store) ?? p.Name,
                    p.GetColumnType(store),
                    p.GetValueGenerationStrategy(store) == SqlServerValueGenerationStrategy.IdentityColumn,
                    p.IsColumnNullable(store),
                    DefaultOf(p, store),
                    p.GetCollation(store)))
                .ToList();

            var indexes = new List<ModelIndex>();
            var key = entity.FindPrimaryKey() ?? throw new InvalidOperationException($"osdu.{name} has no primary key.");
            indexes.Add(new ModelIndex(
                key.GetName(store) ?? $"PK_{name}",
                ColumnList(key.Properties.Select(p => (p.GetColumnName(store) ?? p.Name, false))),
                key.IsClustered(store) == false ? "primary key, nonclustered" : "primary key",
                "none",
                "none"));
            foreach (var index in entity.GetIndexes())
            {
                var descending = index.IsDescending;
                var keyColumns = index.Properties.Select((p, i) => (p.GetColumnName(store) ?? p.Name, descending is not null && i < descending.Count && descending[i]));
                var included = index.GetIncludeProperties()?.Select(n => entity.FindProperty(n)?.GetColumnName(store) ?? n).ToList() ?? [];
                indexes.Add(new ModelIndex(
                    index.GetDatabaseName(store) ?? index.Name ?? string.Join("_", index.Properties.Select(p => p.Name)),
                    ColumnList(keyColumns),
                    (index.IsUnique ? "unique" : "index") + (index.IsClustered(store) == true ? ", clustered" : string.Empty),
                    index.GetFilter(store) is { } filter ? $"`{filter}`" : "none",
                    included.Count == 0 ? "none" : string.Join(", ", included.Select(c => $"`{c}`"))));
            }

            var checks = entity.GetCheckConstraints().Select(c => new ModelCheck(c.Name ?? c.ModelName, c.Sql)).ToList();
            tables.Add(new ModelTable(name, entity.GetSchema(), columns, indexes, checks));
        }

        return tables.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    private static string? DefaultOf(IProperty property, StoreObjectIdentifier store)
    {
        if (property.GetDefaultValueSql(store) is { } sql)
        {
            return sql;
        }

        // GetDefaultValue answers the CLR default of a column the model sets none for; only a configured value is the
        // database's default.
        if (!property.TryGetDefaultValue(store, out var value))
        {
            return null;
        }

        return value switch
        {
            null => "NULL",
            string text => "N'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
            bool flag => flag ? "1" : "0",
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString(),
        };
    }

    private static string ColumnList(IEnumerable<(string Name, bool Descending)> columns)
        => string.Join(", ", columns.Select(c => $"`{c.Name}`" + (c.Descending ? " DESC" : string.Empty)));

    private static string TypeCell(ModelColumn column) => $"`{column.Type}`" + (column.Identity ? " identity" : string.Empty);

    private static string ColumnRow(ModelColumn column)
        => $"| `{column.Name}` | {TypeCell(column)} | {(column.Nullable ? "yes" : "no")} | "
            + (column.Default is { } d ? $"Default `{d}`. " : string.Empty)
            + (column.Collation is { } c ? $"Collation `{c}`. " : string.Empty)
            + "<what it holds> |";

    private static string IndexRow(ModelIndex index)
        => $"| `{index.Name}` | {index.Columns} | {index.Kind} | {index.Filter} | {index.Includes} | <what it serves> |";

    private static string Expected(ModelTable table)
    {
        var text = new StringBuilder();
        text.AppendLine("The rows the model gives, to complete with what each column holds and what each index serves:");
        text.AppendLine();
        text.AppendLine("## Columns");
        text.AppendLine();
        text.AppendLine("| Column | Type | Null | What it holds |");
        text.AppendLine("| --- | --- | --- | --- |");
        foreach (var column in table.Columns)
        {
            text.AppendLine(ColumnRow(column));
        }

        text.AppendLine();
        text.AppendLine("## Keys and indexes");
        text.AppendLine();
        text.AppendLine("| Index | Columns | Kind | Filter | Includes | What it serves |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var index in table.Indexes)
        {
            text.AppendLine(IndexRow(index));
        }

        foreach (var check in table.Checks)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"Check constraint `{check.Name}`: `{check.Sql}`.");
        }

        return text.ToString();
    }

    /// <summary>The frontmatter's scalar keys, unquoted.</summary>
    private static Dictionary<string, string> Frontmatter(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return values;
        }

        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        foreach (var line in text[4..(end < 0 ? text.Length : end)].Split('\n'))
        {
            var match = ScalarLine().Match(line);
            if (match.Success)
            {
                values[match.Groups[1].Value] = match.Groups[2].Value.Trim().Trim('"');
            }
        }

        return values;
    }

    /// <summary>The body rows of the first table under the <c>## heading</c>, each with the number of cells it must have.</summary>
    private static List<string[]> Rows(string text, string heading, int cells, List<string> problems)
    {
        var lines = text.Split('\n');
        var start = Array.FindIndex(lines, l => l == $"## {heading}");
        if (start < 0)
        {
            problems.Add($"the page has no \"## {heading}\" section");
            return [];
        }

        var rows = new List<string[]>();
        var seenHeader = false;
        for (var i = start + 1; i < lines.Length && !lines[i].StartsWith("## ", StringComparison.Ordinal); i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith('|'))
            {
                if (seenHeader && rows.Count > 0)
                {
                    break;
                }

                continue;
            }

            var parts = line.Trim('|').Split('|').Select(p => p.Trim()).ToArray();
            if (!seenHeader)
            {
                seenHeader = true;
                continue;
            }

            if (parts.All(p => p.Length > 0 && p.Trim('-', ':').Length == 0))
            {
                continue;
            }

            if (parts.Length != cells)
            {
                problems.Add($"a row of \"## {heading}\" has {parts.Length} cells, not {cells}: {line}");
                continue;
            }

            rows.Add(parts);
        }

        if (rows.Count == 0)
        {
            problems.Add($"the \"## {heading}\" section has no table rows");
        }

        return rows;
    }

    private static string Unquote(string cell) => cell.Trim('`');

    private static void Expect(List<string> problems, bool holds, string what)
    {
        if (!holds)
        {
            problems.Add(what);
        }
    }

    private static string Relative(string path) => Path.GetRelativePath(RepositoryRoot.Path, path).Replace('\\', '/');

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex UpperCase();

    [GeneratedRegex("^([A-Za-z]+): (.+)$")]
    private static partial Regex ScalarLine();

    private sealed record ModelTable(string Name, string? Schema, IReadOnlyList<ModelColumn> Columns, IReadOnlyList<ModelIndex> Indexes, IReadOnlyList<ModelCheck> Checks);

    private sealed record ModelColumn(string Name, string Type, bool Identity, bool Nullable, string? Default, string? Collation);

    private sealed record ModelIndex(string Name, string Columns, string Kind, string Filter, string Includes);

    private sealed record ModelCheck(string Name, string Sql);
}
