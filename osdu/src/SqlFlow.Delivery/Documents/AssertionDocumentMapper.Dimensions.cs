using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

// The dimensions a flow and its tests name, and their titles (osdu/docs/reference/flow/assertion-sets.md,
// Dimensions every assertion has): each dimension's name and value, a test's laid over its flow's.
internal static partial class AssertionMapper
{
    /// <summary>The most dimensions a flow names, and a test names over its flow's.</summary>
    public const int MaxDimensions = 20;

    /// <summary>The longest value of a dimension, and the longest title.</summary>
    public const int MaxShownText = 200;

    /// <summary>
    /// The dimensions a flow or a test names (osdu/docs/reference/flow/assertion-sets.md, Dimensions), in the order written:
    /// each a name (a letter, then letters, digits, spaces, '_' and '-', at most 40 characters) and a value on one line.
    /// <c>Set</c> is the flow's set, which <c>set:</c> names, so no document names it here.
    /// </summary>
    private static IReadOnlyList<AssertionDimension> MapDimensions(Dictionary<string, string>? declared, string key, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (declared.Count > MaxDimensions)
        {
            throw new FlowValidationException($"{source}: {key} names {declared.Count} dimensions; at most {MaxDimensions}.");
        }

        var dimensions = new List<AssertionDimension>(declared.Count);
        foreach (var (raw, value) in declared)
        {
            var name = raw?.Trim() ?? string.Empty;
            if (!DimensionName().IsMatch(name))
            {
                throw new FlowValidationException(
                    $"{source}: {key} names '{raw}', which is not a dimension name: a letter followed by letters, digits, spaces, '_' and '-', at most 40 characters, such as Country.");
            }

            if (string.Equals(name, AssertionDimensions.Set, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowValidationException($"{source}: {key} names {name}, which is the flow's set; name it with set: instead.");
            }

            if (dimensions.Exists(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlowValidationException($"{source}: {key} names {name} twice, ignoring case; name each dimension once.");
            }

            var text = value?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                throw new FlowValidationException($"{source}: {key}.{name} is empty; give the value the assertions have along it, or take the dimension out.");
            }

            if (text.Length > MaxShownText || text.Any(char.IsControl))
            {
                throw new FlowValidationException($"{source}: {key}.{name} is not a value on one line of at most {MaxShownText} characters.");
            }

            dimensions.Add(new AssertionDimension(name, text));
        }

        return dimensions;
    }

    /// <summary>A test's dimensions: its flow's, each in place, with the test's own value over it, then those only the test names.</summary>
    private static IReadOnlyList<AssertionDimension> MergeDimensions(IReadOnlyList<AssertionDimension> flow, IReadOnlyList<AssertionDimension> test)
    {
        var merged = flow.Select(d => test.FirstOrDefault(t => string.Equals(t.Name, d.Name, StringComparison.OrdinalIgnoreCase)) ?? d).ToList();
        merged.AddRange(test.Where(t => !flow.Any(d => string.Equals(d.Name, t.Name, StringComparison.OrdinalIgnoreCase))));
        return merged;
    }

    /// <summary>A title as a document writes it: on one line, at most <see cref="MaxShownText"/> characters; null when it gives none.</summary>
    private static string? TitleOf(string? declared, string key, string source)
    {
        var title = Optional(declared);
        return title is not null && (title.Length > MaxShownText || title.Any(char.IsControl))
            ? throw new FlowValidationException($"{source}: {key} is not a title on one line of at most {MaxShownText} characters.")
            : title;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9 _-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex DimensionName();
}
