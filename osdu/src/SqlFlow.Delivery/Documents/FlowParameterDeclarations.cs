using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Reads a flow document's <c>parameters</c> the one way every kind reads them (osdu/docs/reference/flow/parameters.md):
/// each parameter's <c>required</c>, <c>default</c>, <c>description</c> and <c>choices</c>, and a delivery flow's
/// <c>lookup</c>, checked as the document loads. A run resolves its values against what this reads, and a run form and a
/// schedule's preview describe the same declaration, so all three agree.
/// </summary>
internal static class FlowParameterDeclarations
{
    /// <summary>The most values one parameter's <c>choices</c> lists.</summary>
    public const int MaxChoices = 500;

    /// <summary>
    /// The parameters <paramref name="declared"/> names, in the document's order. A parameter written with no body
    /// (<c>region:</c>) is optional, with no default. <paramref name="source"/> names the document in errors.
    /// </summary>
    public static IReadOnlyDictionary<string, FlowParameter> Map<TYaml>(Dictionary<string, TYaml>? declared, string source)
        where TYaml : FlowParameterYaml
    {
        var parameters = new Dictionary<string, FlowParameter>(StringComparer.Ordinal);
        foreach (var (name, yaml) in declared ?? [])
        {
            var at = $"{source}: parameters.{name}";
            var choices = Choices(yaml?.Choices, yaml?.Default, at);
            var lookup = yaml is DeliveryParameterYaml { Lookup: { } written } ? Lookup(written, at + ".lookup") : null;
            if (choices is not null && lookup is not null)
            {
                throw new FlowValidationException(
                    $"{at} lists choices and names a lookup; a parameter offers its own list or the values a column holds, so keep one of them.");
            }

            parameters[name] = new FlowParameter
            {
                Required = yaml?.Required ?? false,
                Default = yaml?.Default,
                Description = yaml?.Description,
                Choices = choices,
                Lookup = lookup,
            };
        }

        return parameters;
    }

    /// <summary>
    /// The values a parameter lists under <c>choices</c>, checked: at least one, each written as a run gives it and listed
    /// once, at most <see cref="MaxChoices"/>, and the default among them. Null when the parameter lists none.
    /// </summary>
    private static List<string>? Choices(List<string>? written, string? fallback, string at)
    {
        if (written is null)
        {
            return null;
        }

        if (written.Count == 0)
        {
            throw new FlowValidationException($"{at}.choices lists no values; list the values the parameter may take, or leave choices out.");
        }

        if (written.Count > MaxChoices)
        {
            throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{at}.choices lists {written.Count} values; a parameter lists at most {MaxChoices}. Leave choices out, so a run takes any value."));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var choice in written)
        {
            if (string.IsNullOrWhiteSpace(choice) || choice.Trim().Length != choice.Length)
            {
                throw new FlowValidationException(
                    $"{at}.choices holds '{choice}'; every choice is a value a run gives, written without surrounding space.");
            }

            if (!seen.Add(choice))
            {
                throw new FlowValidationException($"{at}.choices lists '{choice}' more than once.");
            }
        }

        if (fallback is not null && !seen.Contains(fallback))
        {
            throw new FlowValidationException($"{at}.default is '{fallback}', which is not one of its choices: {string.Join(", ", written)}.");
        }

        return written;
    }

    /// <summary>A delivery flow's <c>lookup</c>, as written: the column, and the interface whose record table holds it.</summary>
    private static FlowParameterLookup Lookup(FlowParameterLookupYaml written, string at)
    {
        if (string.IsNullOrWhiteSpace(written.Column))
        {
            throw new FlowValidationException($"{at}.column is missing: name the column of the record table whose values the parameter offers.");
        }

        return new FlowParameterLookup(written.Column, string.IsNullOrWhiteSpace(written.Interface) ? null : written.Interface.Trim());
    }
}
