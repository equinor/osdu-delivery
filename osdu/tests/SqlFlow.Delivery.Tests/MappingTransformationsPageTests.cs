using System.Text.RegularExpressions;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Expressions;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The transformations page (<c>osdu/docs/reference/flow/mapping-transformations.md</c>) is where a mapping's author reads
/// what the engine supports, so it lists exactly what the engine has: every expression function and modifier in its own
/// section and in the page's first table, nothing the engine lacks, and every word of the mapping language. What each
/// one gives is prose the test does not read.
/// </summary>
public sealed partial class MappingTransformationsPageTests
{
    private static string Page { get; } = File.ReadAllText(RepositoryRoot.Combine("osdu", "docs", "reference", "flow", "mapping-transformations.md")).ReplaceLineEndings("\n");

    private static IEnumerable<string> Functions => ExpressionFunctions.All.Select(f => f.Name).Append(ExpressionParser.ChoiceName);

    [Fact]
    public void The_functions_section_lists_exactly_the_functions_of_the_expression_language()
    {
        var expected = ExpressionFunctions.All.Select(f => f.Signature).Append(ExpressionParser.ChoiceSignature).Order(StringComparer.Ordinal);
        Assert.Equal(expected, FirstCells(Section("Expression functions")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_modifiers_section_lists_exactly_the_modifiers_the_loader_reads()
    {
        var listed = FirstCells(Section("Modifiers")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        Assert.Equal(MappingMapper.ModifierNames.Order(StringComparer.Ordinal), listed);
    }

    [Fact]
    public void The_first_table_names_every_function_and_modifier()
    {
        var glance = Section("At a glance");
        var unnamed = Functions.Concat(MappingMapper.ModifierNames).Where(name => !glance.Contains($"`{name}`", StringComparison.Ordinal)).ToList();
        Assert.True(unnamed.Count == 0, $"the page's first table does not name: {string.Join(", ", unnamed)}");
    }

    [Fact]
    public void The_page_names_every_word_of_the_mapping_language()
    {
        var unnamed = MappingMapper.NodeKeys.Where(word => !Page.Contains($"`{word}", StringComparison.Ordinal)).ToList();
        Assert.True(unnamed.Count == 0, $"the page does not name: {string.Join(", ", unnamed)}");
    }

    private static string Section(string heading)
    {
        var start = Page.IndexOf($"\n## {heading}\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"the page has no section '{heading}'.");
        var end = Page.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? Page[start..] : Page[start..end];
    }

    /// <summary>What the first cell of each table row of a section names: the first text it holds in backticks.</summary>
    private static IEnumerable<string> FirstCells(string section) => FirstCell().Matches(section).Select(match => match.Groups[1].Value);

    [GeneratedRegex(@"^\| `([^`]+)`", RegexOptions.Multiline)]
    private static partial Regex FirstCell();
}
