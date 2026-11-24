using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Templates;

// A node's modifiers (osdu/docs/reference/flow/mapping-modifiers.md): what one lacks before it can be written, and
// each as the lines of its list item, with the settings a replace writes beside it.
public static partial class MappingBuilder
{
    private static void WriteModifiers(IReadOnlyList<MappingDraftModifier> modifiers, string inner, Action<string> line)
    {
        if (modifiers.Count == 0)
        {
            return;
        }

        line(inner + MappingMapper.ModifiersKey + ":");
        foreach (var modifier in modifiers)
        {
            var lines = ModifierLines(modifier);
            line(inner + "  - " + lines[0]);
            foreach (var setting in lines.Skip(1))
            {
                line(inner + "    " + setting);
            }
        }
    }

    private static void ModifierIssue(MappingDraftModifier modifier, string? scope, string target, Action<string, string?> error)
    {
        switch (modifier.Kind)
        {
            case "trim" or "upper" or "lower":
                break;
            case "date" when !string.IsNullOrEmpty(modifier.Text) && Rendering.DateValues.FormatProblem(modifier.Text) is { } problem:
                error($"{target}: the date format '{modifier.Text}' {problem}.", target);
                break;
            case "date":
                break;
            case "number" when Rendering.NumberValues.SeparatorsProblem(modifier.DecimalSeparator, modifier.GroupSeparator) is { } separators:
                error($"{target}: number {separators}.", target);
                break;
            case "number":
                break;
            case "split" when string.IsNullOrEmpty(modifier.Separator) || modifier.Part is not > 0:
                error($"{target}: split needs a separator and the part to keep, counting from one.", target);
                break;
            case "split":
                break;
            case "replace" when !string.IsNullOrWhiteSpace(modifier.Table) && modifier.Replacements is { Count: > 0 }:
                error($"{target}: a replace reads its table from the cache or lists its values, not both.", target);
                break;
            case "replace" when !string.IsNullOrWhiteSpace(modifier.Table) && !ColumnName().IsMatch(modifier.Table.Trim()):
                error($"{target}: replace reads cache type '{modifier.Table}', and a cached type is named by letters, digits, '_' and '-', such as UnitAlias.", target);
                break;
            case "replace" when !string.IsNullOrWhiteSpace(modifier.Match) && !FieldPath().IsMatch(modifier.Match.Trim()):
                error($"{target}: replace's match names the cached field a value is compared with, such as mnemonic, not '{modifier.Match}'.", target);
                break;
            case "replace" when !string.IsNullOrWhiteSpace(modifier.Field) && !FieldPath().IsMatch(modifier.Field.Trim()):
                error($"{target}: replace's field names the cached field that replaces a value, such as curve_family, not '{modifier.Field}'.", target);
                break;
            case "replace" when string.IsNullOrWhiteSpace(modifier.Table) && (!string.IsNullOrWhiteSpace(modifier.Match) || !string.IsNullOrWhiteSpace(modifier.Field)):
                error($"{target}: match and field choose the fields of a table read from the cache; choose the cached type too.", target);
                break;
            case "replace" when string.IsNullOrWhiteSpace(modifier.Table)
                && (modifier.Replacements is not { Count: > 0 } || modifier.Replacements.Any(r => string.IsNullOrWhiteSpace(r.From))):
                error($"{target}: replace needs at least one incoming value and what it becomes, or a cached table to read them from.", target);
                break;
            case "replace" when (modifier.Replacements ?? []).GroupBy(r => r.From.Trim(), StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice:
                error($"{target}: replace lists '{twice.Key}' more than once; a value is matched trimmed, so each incoming value is listed once.", target);
                break;
            case "replace" when modifier.OtherwiseKind is not (null or FallbackKeep or FallbackEmpty or FallbackText):
                error($"{target}: replace's otherwise is keep, empty or text, not '{modifier.OtherwiseKind}'.", target);
                break;
            case "replace" when modifier.OtherwiseKind == FallbackText && string.IsNullOrWhiteSpace(modifier.OtherwiseText):
                error($"{target}: replace's otherwise needs the text an unlisted value becomes; choose no value for none.", target);
                break;
            case "replace":
                break;
            case "equals" when string.IsNullOrEmpty(modifier.Text):
                error($"{target}: equals needs the text to compare with.", target);
                break;
            case "equals":
                break;
            case "id" when IdTemplate.TryParse(modifier.Text, scope, out var idProblem) is null:
                error($"{target}: id: {idProblem}.", target);
                break;
            case "id":
                break;
            case "ref" when !string.IsNullOrWhiteSpace(modifier.Text) && !IdTemplate.IsEntityType(modifier.Text.Trim()) && !EntityName().IsMatch(modifier.Text.Trim()):
                error($"{target}: ref '{modifier.Text.Trim()}' is not an entity type; name it as the template does, such as UnitOfMeasure, or in full, such as reference-data--UnitOfMeasure.", target);
                break;
            case "ref":
                break;
            default:
                error($"{target}: '{modifier.Kind}' is not a modifier.", target);
                break;
        }
    }

    private const string FallbackKeep = "keep";

    private const string FallbackEmpty = "empty";

    private const string FallbackText = "text";

    private static string FallbackKind(ReplaceFallback fallback) => fallback.Kind switch
    {
        ReplaceFallbackKind.Keep => FallbackKeep,
        ReplaceFallbackKind.Empty => FallbackEmpty,
        _ => FallbackText,
    };

    /// <summary>
    /// A modifier as the lines of its list item: the modifier itself, then the settings written beside it. Only a replace has
    /// any: the fields of a cached table it names, and its <c>otherwise</c>, written beside the table so no incoming value is
    /// ever read as a setting.
    /// </summary>
    private static IReadOnlyList<string> ModifierLines(MappingDraftModifier modifier)
    {
        var lines = new List<string> { ModifierText(modifier) };
        if (modifier.Kind != "replace")
        {
            return lines;
        }

        if (!string.IsNullOrWhiteSpace(modifier.Table))
        {
            if (!string.IsNullOrWhiteSpace(modifier.Match))
            {
                lines.Add("match: " + modifier.Match.Trim());
            }

            if (!string.IsNullOrWhiteSpace(modifier.Field))
            {
                lines.Add("field: " + modifier.Field.Trim());
            }
        }

        switch (modifier.OtherwiseKind)
        {
            case FallbackEmpty:
                lines.Add("otherwise: ~");
                break;
            case FallbackText when !string.IsNullOrWhiteSpace(modifier.OtherwiseText):
                lines.Add("otherwise: " + Scalar(modifier.OtherwiseText));
                break;
        }

        return lines;
    }

    /// <summary>A replacement as a flow scalar: its text, or <c>~</c> for no value.</summary>
    private static string ReplacementText(string? to) => to is null ? "~" : FlowScalar(to);

    private static string ModifierText(MappingDraftModifier modifier) => modifier.Kind switch
    {
        "split" => "split: { separator: " + FlowScalar(modifier.Separator ?? string.Empty) + ", part: " + (modifier.Part ?? 0).ToString(CultureInfo.InvariantCulture) + " }",
        "replace" when !string.IsNullOrWhiteSpace(modifier.Table) => $"replace: {MappingMapper.CacheReference}.{modifier.Table.Trim()}",
        "replace" => "replace: { " + string.Join(", ", (modifier.Replacements ?? []).Select(r => FlowScalar(r.From) + ": " + ReplacementText(r.To))) + " }",
        "equals" => "equals: " + Scalar(modifier.Text ?? string.Empty),
        "date" when !string.IsNullOrEmpty(modifier.Text) => "date: " + Scalar(modifier.Text),
        "id" => "id: " + Quote(modifier.Text ?? string.Empty),
        "ref" when !string.IsNullOrWhiteSpace(modifier.Text) => "ref: " + Scalar(modifier.Text.Trim()),
        "number" when modifier.GroupSeparator is not null || modifier.DecimalSeparator is not (null or ".")
            => "number: { decimal: " + FlowScalar(modifier.DecimalSeparator ?? ".") + (modifier.GroupSeparator is null ? string.Empty : ", group: " + FlowScalar(modifier.GroupSeparator)) + " }",
        _ => modifier.Kind,
    };

    /// <summary>The name of an OSDU entity, as a relationship names it after its group.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex EntityName();
}
