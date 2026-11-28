using System.Text;

namespace SqlFlow.Delivery.Templates;

/// <summary>
/// Whether a schema marks a property deprecated, as the OSDU data definitions write it: its description starts with
/// <c>DEPRECATED</c>, then says what to use instead (WellLog 1.5.0's LogRun: <c>DEPRECATED: Use the Well Log Acquisition
/// schema `LogRun[]` array and the `LogRunID`. Log Run - ...</c>). Only the description changes, so a record that writes the
/// property is still accepted; the data definitions call such a change documentation-only (Guides/Chapters/94-KnownIssues.md).
/// </summary>
public static class TemplateDeprecation
{
    private const string Marker = "DEPRECATED";

    /// <summary>The longest guidance kept; a longer first sentence is cut at a word and marked as cut.</summary>
    private const int MaxGuidance = 280;

    /// <summary>Whether <paramref name="description"/> marks its property deprecated.</summary>
    public static bool IsDeprecated(string? description)
        => description is not null && description.AsSpan().TrimStart().StartsWith(Marker, StringComparison.Ordinal);

    /// <summary>
    /// What a deprecated property's description says to do instead: its first sentence after the marker, on one line
    /// (<c>Use the `SamplingStart` property.</c>). A full stop inside a code span or a dotted name ends nothing. Null for a
    /// description that does not mark its property deprecated, or that says nothing after the marker.
    /// </summary>
    public static string? Guidance(string? description)
    {
        if (!IsDeprecated(description))
        {
            return null;
        }

        var rest = description!.TrimStart()[Marker.Length..].TrimStart(':', ' ', '\t', '\r', '\n');
        var sentence = new StringBuilder();
        var inCode = false;
        for (var i = 0; i < rest.Length; i++)
        {
            var c = rest[i];
            sentence.Append(char.IsWhiteSpace(c) ? ' ' : c);
            inCode ^= c == '`';
            if (c == '.' && !inCode && (i + 1 == rest.Length || char.IsWhiteSpace(rest[i + 1])))
            {
                break;
            }
        }

        var text = string.Join(' ', sentence.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length <= MaxGuidance)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', MaxGuidance);
        return text[..(cut > 0 ? cut : MaxGuidance)] + " ...";
    }
}
