using System.Globalization;
using SqlFlow.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// The texts a mapping's modifiers compare with or write (a replace's keys and values and its otherwise, equals, split's
/// separator) are text, and YAML reads an unquoted <c>01</c>, <c>1.0</c>, <c>1e3</c> or <c>+7</c> as a number, which the
/// modifier reads back as <c>1</c>, <c>1</c>, <c>1000</c> or <c>7</c>: a value the mapping did not write, delivered without
/// a word. Such a value is refused, naming the line and asking for it to be quoted. One YAML reads back as it is written
/// (an unquoted <c>1</c>, <c>2.5</c> or <c>true</c>) is taken as it is, so mappings that say what they mean are untouched
/// (osdu/docs/reference/flow/mapping-modifiers.md).
/// </summary>
/// <remarks>
/// The document is read twice: as YAML's own nodes, which keep each scalar as written and whether it was quoted, and as the
/// values the mapping is read from. The two are walked together, so a value is judged by exactly what the modifier reads.
/// </remarks>
internal static class ModifierTexts
{
    /// <summary>Refuses a modifier text YAML reads as a value other than the one written.</summary>
    /// <param name="yaml">The mapping document.</param>
    /// <param name="values">The document as the mapping is read from it: maps, lists and scalars typed as YAML reads them.</param>
    /// <param name="source">The document's name, for the message.</param>
    public static void Refuse(string yaml, object? values, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException)
        {
            // The document was read as values already; a text YAML's own reader refuses says nothing about the modifiers.
            return;
        }

        if (stream.Documents.Count > 0)
        {
            Walk(stream.Documents[0].RootNode, values, source);
        }
    }

    private static void Walk(YamlNode node, object? value, string source)
    {
        switch (node)
        {
            case YamlMappingNode map when value is IDictionary<object, object> read && read.Count == map.Children.Count:
                foreach (var ((key, child), (_, childValue)) in map.Children.Zip(read))
                {
                    if (key is YamlScalarNode { Value: MappingMapper.ModifiersKey } && child is YamlSequenceNode modifiers && childValue is IList<object> list && list.Count == modifiers.Children.Count)
                    {
                        foreach (var (modifier, modifierValue) in modifiers.Children.Zip(list))
                        {
                            Modifier(modifier, modifierValue, source);
                        }

                        continue;
                    }

                    Walk(child, childValue, source);
                }

                break;
            case YamlSequenceNode sequence when value is IList<object> items && items.Count == sequence.Children.Count:
                foreach (var (item, itemValue) in sequence.Children.Zip(items))
                {
                    Walk(item, itemValue, source);
                }

                break;
        }
    }

    /// <summary>One item of a <c>$modifiers</c> list: the settings of it that are texts.</summary>
    private static void Modifier(YamlNode node, object? value, string source)
    {
        if (node is not YamlMappingNode map || value is not IDictionary<object, object> read || read.Count != map.Children.Count)
        {
            return;
        }

        foreach (var ((key, setting), (_, settingValue)) in map.Children.Zip(read))
        {
            switch ((key as YamlScalarNode)?.Value)
            {
                case "replace" when setting is YamlMappingNode table && settingValue is IDictionary<object, object> pairs && pairs.Count == table.Children.Count:
                    foreach (var ((from, to), (fromValue, toValue)) in table.Children.Zip(pairs))
                    {
                        // A key is matched, ignoring case where the table does; a value is written as it reads.
                        Check(from, fromValue, "replace key", Convert.ToString, StringComparison.OrdinalIgnoreCase, source);
                        Check(to, toValue, "replace value", Lowered, StringComparison.Ordinal, source);
                    }

                    break;
                case "otherwise":
                    Check(setting, settingValue, "otherwise", Lowered, StringComparison.Ordinal, source);
                    break;
                case "equals":
                    // equals compares ignoring case, so a value that changes only its case compares the same.
                    Check(setting, settingValue, "equals", Convert.ToString, StringComparison.OrdinalIgnoreCase, source);
                    break;
                case "split" when setting is YamlMappingNode options && settingValue is IDictionary<object, object> read2 && read2.Count == options.Children.Count:
                    foreach (var ((option, optionNode), (_, optionValue)) in options.Children.Zip(read2))
                    {
                        if (option is YamlScalarNode { Value: "separator" })
                        {
                            Check(optionNode, optionValue, "split separator", Convert.ToString, StringComparison.Ordinal, source);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>A value as a replace value and otherwise read it: a boolean in lower case, anything else as its invariant text.</summary>
    private static string? Lowered(object? value, IFormatProvider provider)
        => value is bool flag ? (flag ? "true" : "false") : Convert.ToString(value, provider);

    private static void Check(
        YamlNode node, object? value, string setting, Func<object?, IFormatProvider, string?> read, StringComparison comparison, string source)
    {
        // Only an unquoted scalar YAML typed is read back from a value: a quoted one, and one read as text, stay as written,
        // and ~ is the no value the mapping wrote.
        if (node is not YamlScalarNode { Style: ScalarStyle.Plain } scalar || scalar.Value is null || value is null or string)
        {
            return;
        }

        var text = read(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.Equals(text, scalar.Value, comparison))
        {
            return;
        }

        var reads = value is bool ? "a boolean" : "a number";
        throw new FlowValidationException(
            $"{source}: line {scalar.Start.Line}: the {setting} {scalar.Value} is written without quotes, so YAML reads it as {reads}, which the modifier reads back as '{text}', not as written; quote it, '{scalar.Value}', to keep the text.");
    }
}
