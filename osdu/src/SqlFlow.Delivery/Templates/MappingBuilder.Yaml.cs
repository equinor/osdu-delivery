using System.Globalization;
using System.Text;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Templates;

// The draft written as a mapping document, in the documented style: the header, then the record tree property by
// property, each entry a literal or a node of the mapping language with its settings, then the lookups and the rules
// on the rows.
public static partial class MappingBuilder
{
    /// <summary>
    /// The draft written as a mapping document, in the documented style: the header, then the record tree, each entry at
    /// the place its target names. Incomplete values are written as they stand, so the page shows what
    /// was typed and the loader reports it; an entry that has no place in the tree is left out with a comment saying why,
    /// and <see cref="Incomplete"/> reports it.
    /// </summary>
    public static string ToYaml(MappingDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var yaml = new StringBuilder();
        void Line(string text) => yaml.Append(text).Append('\n');

        Line("documentType: mapping");
        Line("name: " + Scalar(draft.Name));
        Line("version: " + Scalar(draft.Version));
        Line("template:");
        Line("  kind: " + Scalar(draft.TemplateKind));
        Line("  version: " + Scalar(draft.TemplateVersion));
        if (!string.IsNullOrWhiteSpace(draft.Description))
        {
            Line("description: " + Scalar(draft.Description.Trim()));
        }

        Line(string.Empty);
        Line("dataset:");
        Line("  system: " + Scalar(draft.System));
        Line("  key: [" + string.Join(", ", draft.Key.Select(k => FlowScalar(k.Trim()))) + "]");
        if (!string.IsNullOrWhiteSpace(draft.Label))
        {
            Line("  label: " + Scalar(draft.Label.Trim()));
        }

        if (draft.Identity.Count > 0)
        {
            Line("  identity: [" + string.Join(", ", draft.Identity.Select(k => FlowScalar(k.Trim()))) + "]");
        }

        if (draft.IdFrom is { } idFrom)
        {
            Line("  idFrom: " + Scalar(idFrom));
        }

        if (draft.Rules.Count > 0)
        {
            Line("  assert:");
            foreach (var rule in draft.Rules)
            {
                WriteRule(rule, "    ", Line);
            }
        }

        Line(string.Empty);
        Line("parameters:");
        foreach (var parameter in draft.Parameters)
        {
            Line("  " + Scalar(parameter.Name) + ":");
            Line("    required: " + (parameter.Required ? "true" : "false"));
            if (parameter.Default is not null)
            {
                Line("    default: " + Scalar(parameter.Default));
            }

            if (!string.IsNullOrWhiteSpace(parameter.Description))
            {
                Line("    description: " + Scalar(parameter.Description.Trim()));
            }
        }

        if (draft.Searches.Count > 0)
        {
            Line(string.Empty);
            Line("searches:");
            foreach (var search in draft.Searches)
            {
                Line("  " + Scalar(search.Name) + ":");
                Line("    kind: " + Scalar(search.Kind));
                Line("    schema:");
                Line("      kind: " + Scalar(search.SchemaKind));
                Line("      version: " + Scalar(search.SchemaVersion));
                if (!string.IsNullOrWhiteSpace(search.Description))
                {
                    Line("    description: " + Scalar(search.Description.Trim()));
                }
            }
        }

        if (draft.Lookups.Count > 0)
        {
            Line(string.Empty);
            Line("lookups:");
            foreach (var lookup in draft.Lookups)
            {
                WriteLookup(lookup, Line);
            }
        }

        if (!string.IsNullOrWhiteSpace(draft.RestrictedAccess))
        {
            // The access a record goes out under while a value an entry marks $restrictWhenMissing is missing.
            Line(string.Empty);
            Line(MappingMapper.RestrictedAccessKey + ": " + Scalar(draft.RestrictedAccess.Trim()));
        }

        Line(string.Empty);
        Line("record:");
        var layout = Layout(draft);
        foreach (var left in layout.LeftOut)
        {
            Line($"  # Left out: {Comment(left.Reason ?? $"{left.Target} has another entry, which is written")}.");
        }

        WriteProperties(layout.Root, 2, null, Line);
        return yaml.ToString();
    }

    private static void WriteProperties(List<(string Name, TreeSlot Slot)> properties, int indent, string? scope, Action<string> line)
    {
        var pad = new string(' ', indent);
        foreach (var (name, slot) in properties)
        {
            var key = pad + PropertyKey(name);
            if (slot.Entry is null)
            {
                line(key + ":");
                WriteProperties(slot.Properties!, indent + 2, scope, line);
                continue;
            }

            WriteNode(key, slot, indent, scope, line);
        }
    }

    /// <summary>A record property as a key of the tree: its name, with one more '$' when the name starts with one.</summary>
    private static string PropertyKey(string name) => Scalar(MappingMapper.KeyOfProperty(name));

    /// <summary>One entry at its property: a literal as the value it is, or a node of the mapping language with its settings.</summary>
    private static void WriteNode(string key, TreeSlot slot, int indent, string? scope, Action<string> line)
    {
        var entry = slot.Entry!;
        var inner = new string(' ', indent + 2);
        if (entry.Input == MappingDraftInput.Static)
        {
            WriteLiteral(key, entry, indent, scope, line);
            return;
        }

        if (entry.Input == MappingDraftInput.Repeat)
        {
            line(key + ":");
            line(inner + MappingMapper.ForEachKey + ": " + Scalar(entry.Child?.Trim() ?? string.Empty));
            if (!string.IsNullOrWhiteSpace(entry.Where))
            {
                line(inner + MappingMapper.WhereKey + ": " + ExpressionScalar(entry.Where.Trim()));
            }

            WriteSettings(entry, inner, line, scope);
            if (slot.Properties is { Count: > 0 } items)
            {
                line(inner + MappingMapper.ItemKey + ":");
                WriteProperties(items, indent + 4, string.IsNullOrWhiteSpace(entry.Child) ? null : entry.Child.Trim(), line);
            }
            else
            {
                line(inner + MappingMapper.ItemKey + ": {}");
            }

            return;
        }

        if (entry.Input == MappingDraftInput.Coalesce)
        {
            // The alternatives in the order they are tried, each a node of its own; the node's settings decide for all of them.
            line(key + ":");
            line(inner + MappingMapper.CoalesceKey + ":");
            foreach (var alternative in entry.Alternatives)
            {
                WriteAlternative(alternative, indent + 4, scope, line);
            }

            WriteSettings(entry, inner, line, scope);
            return;
        }

        if (entry.Input == MappingDraftInput.Expression)
        {
            // Always the block form: an expression reads best on a line of its own, and a flow mapping would need it quoted.
            line(key + ":");
            line(inner + MappingMapper.ExprKey + ": " + ExpressionScalar(entry.Expression?.Trim() ?? string.Empty));
            WriteModifiers(entry.Modifiers, inner, line);
            WriteSettings(entry, inner, line, scope);
            return;
        }

        if (entry.Input == MappingDraftInput.List)
        {
            // The items alone, in order: a list has no settings of its own, and each item carries its own.
            if (entry.Items.Count == 0)
            {
                line(key + ": []");
                return;
            }

            line(key + ":");
            foreach (var item in entry.Items)
            {
                if (item.Input == MappingDraftInput.Group)
                {
                    WriteObjectItem(item, entry.Target.Trim(), indent + 2, scope, line);
                }
                else
                {
                    WriteItem(item, indent + 2, scope, valueNode: false, line);
                }
            }

            return;
        }

        if (entry.Input == MappingDraftInput.Lookup)
        {
            // How the record is found is the lookup's to say, so the node holds the field it reads and its own settings alone.
            var field = $"{entry.Lookup?.Trim()}.{entry.CacheField?.Trim()}";
            if (string.IsNullOrWhiteSpace(entry.When) && entry.Required && !entry.RestrictWhenMissing && !entry.Unverified && string.IsNullOrWhiteSpace(entry.Description) && entry.Assertions.Count == 0)
            {
                line(key + ": { " + MappingMapper.LookupKey + ": " + FlowScalar(field) + " }");
                return;
            }

            line(key + ":");
            line(inner + MappingMapper.LookupKey + ": " + Scalar(field));
            WriteSettings(entry, inner, line, scope);
            return;
        }

        var (sourceKey, read) = entry.Input switch
        {
            MappingDraftInput.Dataset => (MappingMapper.FromKey, ScopedColumn(entry.Column ?? string.Empty, scope)),
            MappingDraftInput.Search => (MappingMapper.SearchKey, entry.CacheType?.Trim() ?? string.Empty),
            MappingDraftInput.Parent => (MappingMapper.ParentKey, entry.ParentPath?.Trim() ?? string.Empty),
            MappingDraftInput.Child => (MappingMapper.ChildKey, entry.ChildKind?.Trim() ?? string.Empty),
            _ => (MappingMapper.CacheKey, $"{entry.CacheType?.Trim()}.{entry.CacheField?.Trim()}"),
        };

        // A child link is never required, and the node says so by leaving $required out.
        var findAll = entry.Input == MappingDraftInput.Cache ? entry.FindAll : null;
        var bare = entry.FindBy.Count == 0 && findAll is null && entry.Modifiers.Count == 0 && string.IsNullOrWhiteSpace(entry.When)
            && (entry.Required || entry.Input == MappingDraftInput.Child) && !entry.RestrictWhenMissing
            && !(entry.Input == MappingDraftInput.Cache && entry.IgnoreSeparators) && !entry.Unverified && string.IsNullOrWhiteSpace(entry.Description)
            && entry.Assertions.Count == 0;
        if (bare)
        {
            line(key + ": { " + sourceKey + ": " + FlowScalar(read) + " }");
            return;
        }

        line(key + ":");
        line(inner + sourceKey + ": " + Scalar(read));
        if (findAll is not null)
        {
            WriteLines(MappingMapper.FindAllKey, FindAllLines(findAll, scope), inner, line);
        }
        else if (entry.Input is MappingDraftInput.Cache or MappingDraftInput.Search)
        {
            WriteLines(MappingMapper.FindByKey, entry.FindBy.Select(f => FindByText(f, scope)).ToList(), inner, line);
        }

        WriteModifiers(entry.Modifiers, inner, line);
        WriteSettings(entry, inner, line, scope);
    }

    /// <summary>
    /// A lookup under <c>lookups</c>: the cached type its record is found in, the findBy lines, read against the dataset's
    /// own row, then the modifiers, the separator fold and the description.
    /// </summary>
    private static void WriteLookup(MappingDraftLookup lookup, Action<string> line)
    {
        const string Inner = "    ";
        line("  " + Scalar(lookup.Name?.Trim() ?? string.Empty) + ":");
        line(Inner + MappingMapper.CacheKey + ": " + Scalar(lookup.CacheType?.Trim() ?? string.Empty));
        WriteLines(MappingMapper.FindByKey, lookup.FindBy.Select(f => FindByText(f, null)).ToList(), Inner, line);
        WriteModifiers(lookup.Modifiers, Inner, line);
        if (lookup.IgnoreSeparators)
        {
            line(Inner + MappingMapper.IgnoreSeparatorsKey + ": true");
        }

        if (!string.IsNullOrWhiteSpace(lookup.Description))
        {
            line(Inner + MappingMapper.DescriptionKey + ": " + Scalar(lookup.Description.Trim()));
        }
    }

    /// <summary>A key holding lines of the mapping language (<c>$findBy</c>, <c>$findAll</c>): one line beside the key, several as a list under it.</summary>
    private static void WriteLines(string key, IReadOnlyList<string> lines, string inner, Action<string> line)
    {
        if (lines.Count == 1)
        {
            line(inner + key + ": " + Scalar(lines[0]));
        }
        else if (lines.Count > 1)
        {
            line(inner + key + ":");
            foreach (var text in lines)
            {
                line(inner + "  - " + Scalar(text));
            }
        }
    }

    /// <summary>
    /// A find all as the document writes it: the key line, comparing the field with a column in the entry's scope, a
    /// quoted text or <c>$lookup.&lt;lookup&gt;.&lt;field&gt;</c>, then one <c>&lt;field&gt; is empty</c> line per field,
    /// then one <c>&lt;field&gt; contains &lt;field&gt;</c> line per comparison of two fields.
    /// </summary>
    private static List<string> FindAllLines(MappingDraftFindAll findAll, string? scope)
    {
        var operand = !string.IsNullOrWhiteSpace(findAll.Literal)
            ? QuotedText(findAll.Literal)
            : !string.IsNullOrWhiteSpace(findAll.Lookup)
                ? MappingMapper.LookupReference + "." + findAll.Lookup.Trim()
                : ScopedColumn(findAll.Column ?? string.Empty, scope);
        return
        [
            $"{findAll.Field?.Trim()} = {operand}",
            .. findAll.Empty.Select(field => $"{field?.Trim()} is empty"),
            .. findAll.Contains.Select(line => $"{line.Field?.Trim()} {FindAllContains.Word} {line.Part?.Trim()}"),
        ];
    }

    /// <summary>
    /// One alternative of a coalesce entry, as an item of its <c>$coalesce</c> list at <paramref name="indent"/>: written as a
    /// node of its own is, with a fixed value always under <c>$value</c>, since a bare value in the list would not be a node.
    /// What only the whole node takes is never written here.
    /// </summary>
    private static void WriteAlternative(MappingDraftEntry alternative, int indent, string? scope, Action<string> line)
        => WriteItem(alternative with { When = null, Required = true, Description = null }, indent, scope, valueNode: true, line);

    /// <summary>
    /// One item of a list at <paramref name="indent"/>: a node written as it would be at a key of its own, with the item's
    /// dash in place of the key. A fixed value is the bare value, unless it carries a condition or a description, or
    /// <paramref name="valueNode"/> asks for a node, which puts it under <c>$value</c>.
    /// </summary>
    private static void WriteItem(MappingDraftEntry item, int indent, string? scope, bool valueNode, Action<string> line)
    {
        var dash = new string(' ', indent) + "- ";
        if (item.Input == MappingDraftInput.Static)
        {
            WriteLiteralItem(item, indent, valueNode, line);
            return;
        }

        var written = new List<string>();
        var key = new string(' ', indent) + "-";
        WriteNode(key, new TreeSlot { Entry = item }, indent, scope, written.Add);
        if (written[0].StartsWith(key + ": ", StringComparison.Ordinal))
        {
            // The one-line form: the node itself is the item.
            line(dash + written[0][(key.Length + 2)..]);
            return;
        }

        // The block form: the node's first key moves up beside the dash, and the rest keep their place under it.
        line(dash + written[1].TrimStart());
        foreach (var text in written.Skip(2))
        {
            line(text);
        }
    }

    /// <summary>
    /// An object item of a list of objects at <paramref name="indent"/>: its properties laid out as the record tree lays out
    /// an object's, each at the place its target names inside the items of <paramref name="list"/>, with the item's dash
    /// beside the first. A property the item has no place for is left out with a comment saying why, which the checks report.
    /// </summary>
    private static void WriteObjectItem(MappingDraftEntry item, string list, int indent, string? scope, Action<string> line)
    {
        var properties = new List<(string Name, TreeSlot Slot)>();
        var leftOut = new List<LeftOutEntry>();
        foreach (var property in item.Properties)
        {
            var text = property.Target?.Trim() ?? string.Empty;
            if (TemplatePath.TryParse(text, out var path, out _) && path!.Repeater?.Text == list)
            {
                Place(properties, path.WithinItem, property, path.Text, leftOut);
            }
            else
            {
                leftOut.Add(new LeftOutEntry(text, $"{(text.Length == 0 ? "a property naming no variable" : text)} is not a variable of the items of {list}"));
            }
        }

        var pad = new string(' ', indent);
        foreach (var left in leftOut)
        {
            line($"{pad}# Left out: {Comment(left.Reason ?? $"{left.Target} has another entry in the item, which is written")}.");
        }

        var written = new List<string>();
        WriteProperties(properties, indent + 2, scope, written.Add);
        if (written.Count == 0)
        {
            // An item with no property is written as the empty object it is; the loader refuses it, and the checks name it.
            line(pad + "- {}");
            return;
        }

        WriteDashed(written, indent, line);
    }

    /// <summary>
    /// The settings a node writes after its value: its condition, whether it may be left out, its assertions, and its
    /// description. <paramref name="scope"/> is the child dataset whose rows the node reads, which the columns of an incoming
    /// assertion's conditions are written in.
    /// </summary>
    private static void WriteSettings(MappingDraftEntry entry, string inner, Action<string> line, string? scope = null)
    {
        if (!string.IsNullOrWhiteSpace(entry.When))
        {
            line(inner + MappingMapper.WhenKey + ": " + ExpressionScalar(entry.When.Trim()));
        }

        if (entry.Input is not (MappingDraftInput.Static or MappingDraftInput.Child) && !entry.Required)
        {
            line(inner + MappingMapper.RequiredKey + ": false");
        }

        if (entry.RestrictWhenMissing)
        {
            line(inner + MappingMapper.RestrictWhenMissingKey + ": true");
        }

        if (entry.Input == MappingDraftInput.Cache && entry.IgnoreSeparators)
        {
            line(inner + MappingMapper.IgnoreSeparatorsKey + ": true");
        }

        if (entry.Unverified)
        {
            line(inner + MappingMapper.UnverifiedKey + ": true");
        }

        if (entry.Assertions.Count > 0)
        {
            line(inner + MappingMapper.AssertKey + ":");
            foreach (var assertion in entry.Assertions)
            {
                WriteAssertion(assertion, inner + "  ", scope, line);
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            line(inner + MappingMapper.DescriptionKey + ": " + Scalar(entry.Description.Trim()));
        }
    }

    /// <summary>A findBy line as the mapping writes it: each comparison <c>field = operand</c>, joined by <c>and</c>.</summary>
    private static string FindByText(MappingDraftFind find, string? scope)
        => string.Join(" and ", find.Comparisons.Select(comparison =>
        {
            var operand = !string.IsNullOrWhiteSpace(comparison.Literal)
                ? QuotedText(comparison.Literal)
                : ScopedColumn(comparison.Column ?? string.Empty, scope);
            return $"{comparison.Field?.Trim()} = {operand}";
        }));

    /// <summary>A fixed text a findBy or find all line compares with, in the quotes that do not occur in it.</summary>
    private static string QuotedText(string text) => text.Contains('\'', StringComparison.Ordinal) ? "\"" + text + "\"" : "'" + text + "'";

    /// <summary>
    /// An expression as a YAML value: plain where YAML reads it back as the same text (<c>curve_id != "MD"</c>), which is
    /// most expressions, and otherwise in single quotes, which keep the double quotes an expression writes its texts in as
    /// they stand. A plain value starts with a letter, a digit, '_', '$' or '(', and holds no ": " and no " #", which YAML
    /// would read as a key or a comment.
    /// </summary>
    internal static string ExpressionScalar(string value)
    {
        var plain = value.Length > 0
            && (char.IsAsciiLetterOrDigit(value[0]) || value[0] is '_' or '$' or '(')
            && value.Trim().Length == value.Length
            && value.AsSpan().IndexOfAny('\r', '\n', '\t') < 0
            && !value.Contains(": ", StringComparison.Ordinal)
            && !value.Contains(" #", StringComparison.Ordinal)
            && !value.EndsWith(':')
            && !ReservedWords.Contains(value)
            && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        if (plain)
        {
            return value;
        }

        return value.AsSpan().IndexOfAny('\r', '\n') >= 0 ? Quote(value) : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
