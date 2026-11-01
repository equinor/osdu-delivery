using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Where one thing a dimension declares is written in its flow document: what it is (<see cref="DimensionYamlSource"/>
/// names the targets) and the span of text it takes, lines and columns from 1, the end column the one after the last
/// character.
/// </summary>
public sealed record DimensionYamlSpan(string Target, int Line, int Column, int EndLine, int EndColumn);

/// <summary>
/// A dimension's declaration as its flow document writes it: the lines of its item under <c>dimensions</c>, comments
/// included, from <paramref name="FirstLine"/> of the document, and where each thing it declares is written.
/// <paramref name="Cut"/> is true when the item is longer than a block shows, and its last lines are left out.
/// </summary>
public sealed record DimensionYamlBlock(int FirstLine, IReadOnlyList<string> Lines, IReadOnlyList<DimensionYamlSpan> Spans, bool Cut);

/// <summary>
/// Finds a dimension or a view in its flow document, so a page can show the YAML a person wrote beside what it does. The
/// targets a span of a dimension names are its own keys (<c>kind</c>, <c>path</c>, <c>label</c>, ...), an item of a list
/// by its place (<c>label.0</c>, <c>clean.1</c>), an attribute by its name (<c>attributes.Country</c>, its paths
/// <c>attributes.Country.0</c>, a collected one's path <c>attributes.Source.collect</c>), and a column's name
/// (<c>columns.key</c>, <c>columns.value</c>). Those of a view are its keys (<c>from</c>, ...), each join by its place and
/// its settings (<c>join.0</c>, <c>join.0.on</c>), and each column by its name and its settings (<c>columns.TopDepth</c>,
/// <c>columns.TopDepth.dataType</c>). The document is read for its layout only; what it means is the loader's.
/// </summary>
public static class DimensionYamlSource
{
    /// <summary>The most lines a block holds; a longer item is cut, saying so.</summary>
    public const int MaxLines = 400;

    /// <summary>
    /// The block of <paramref name="yaml"/> that declares the dimension <paramref name="name"/> (compared ignoring case, as a
    /// flow names its dimensions), or null when the document does not parse or declares no such dimension.
    /// </summary>
    public static DimensionYamlBlock? Locate(string yaml, string name)
        => Block(yaml, "dimensions", name, DimensionSpans);

    /// <summary>
    /// The block of <paramref name="yaml"/> that declares the view <paramref name="name"/> (compared ignoring case), or null
    /// when the document does not parse or declares no such view.
    /// </summary>
    public static DimensionYamlBlock? LocateView(string yaml, string name)
        => Block(yaml, "views", name, ViewSpans);

    /// <summary>The spans of a view's item: its keys, each join and its settings, each column and its settings.</summary>
    private static void ViewSpans(Text text, YamlMappingNode item, List<DimensionYamlSpan> spans)
    {
        foreach (var (key, value) in item.Children)
        {
            if (key is not YamlScalarNode { Value: { } field })
            {
                continue;
            }

            spans.Add(text.Entry(field, key, value));
            switch (field)
            {
                case "join" when value is YamlSequenceNode joins:
                    var index = 0;
                    foreach (var join in joins.Children)
                    {
                        var target = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"join.{index}");
                        if (join is YamlMappingNode settings)
                        {
                            text.Entries(target, settings, spans);
                        }

                        index++;
                    }

                    text.Items(field, value, spans);
                    break;
                case "columns" when value is YamlMappingNode columns:
                    foreach (var (columnKey, column) in columns.Children)
                    {
                        if (columnKey is not YamlScalarNode { Value: { } columnName })
                        {
                            continue;
                        }

                        spans.Add(text.Entry("columns." + columnName, columnKey, column));
                        if (column is YamlMappingNode settings)
                        {
                            text.Entries("columns." + columnName, settings, spans);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// The block of <paramref name="yaml"/> that declares the item named <paramref name="name"/> of the list
    /// <paramref name="section"/>, its spans laid out by <paramref name="spanned"/>.
    /// </summary>
    private static DimensionYamlBlock? Block(string yaml, string section, string name, Action<Text, YamlMappingNode, List<DimensionYamlSpan>> spanned)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlException)
        {
            return null;
        }

        if (stream.Documents.Count == 0
            || stream.Documents[0].RootNode is not YamlMappingNode root
            || Value(root, section) is not YamlSequenceNode items)
        {
            return null;
        }

        var item = items.Children.OfType<YamlMappingNode>()
            .FirstOrDefault(d => Value(d, "name") is YamlScalarNode n && string.Equals(n.Value?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return null;
        }

        var text = new Text(yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        var spans = new List<DimensionYamlSpan>();
        spanned(text, item, spans);
        var lines = text.Lines;
        var first = (int)item.Start.Line;
        var last = Math.Min(lines.Count, Math.Max(first, spans.Count == 0 ? first : spans.Max(s => s.EndLine)));

        // The comments written just above the item, as deep as its dash, are about it; so are those after its last value and
        // deeper than its dash, up to the next item.
        var dash = Indent(lines[first - 1]);
        while (first > 1 && IsComment(lines[first - 2]) && Indent(lines[first - 2]) >= dash)
        {
            first--;
        }

        while (last < lines.Count && IsComment(lines[last]) && Indent(lines[last]) > dash)
        {
            last++;
        }

        var cut = last - first + 1 > MaxLines;
        if (cut)
        {
            last = first + MaxLines - 1;
        }

        var shown = lines.Skip(first - 1).Take(last - first + 1).ToList();
        return new DimensionYamlBlock(first, shown, spans.Where(s => s.Line <= last).ToList(), cut);
    }

    /// <summary>The spans of a dimension's item: its keys, each item of its lists, each attribute and its paths, each column's name.</summary>
    private static void DimensionSpans(Text text, YamlMappingNode item, List<DimensionYamlSpan> spans)
    {
        foreach (var (key, value) in item.Children)
        {
            if (key is not YamlScalarNode { Value: { } field })
            {
                continue;
            }

            spans.Add(text.Entry(field, key, value));
            switch (field)
            {
                case "label" or "clean" or "partitions":
                    text.Items(field, value, spans);
                    break;
                case "columns" when value is YamlMappingNode columns:
                    text.Entries(field, columns, spans);
                    break;
                case "elements" when value is YamlMappingNode elements:
                    text.Entries(field, elements, spans);
                    foreach (var (elementsKey, fields) in elements.Children)
                    {
                        if (elementsKey is YamlScalarNode { Value: "fields" } && fields is YamlMappingNode named)
                        {
                            text.Entries(field + ".fields", named, spans);
                        }
                    }

                    break;
                case "attributes" when value is YamlMappingNode attributes:
                    foreach (var (attributeKey, attribute) in attributes.Children)
                    {
                        if (attributeKey is not YamlScalarNode { Value: { } attributeName })
                        {
                            continue;
                        }

                        var target = "attributes." + attributeName;
                        spans.Add(text.Entry(target, attributeKey, attribute));
                        switch (attribute)
                        {
                            case YamlScalarNode or YamlSequenceNode:
                                text.Items(target, attribute, spans);
                                break;
                            case YamlMappingNode settings:
                                text.Entries(target, settings, spans);

                                // The paths of { path: ... } are the attribute's own steps, so they are indexed by place as
                                // the bare form's are, and the blueprint points at the same lines either way.
                                foreach (var (settingKey, setting) in settings.Children)
                                {
                                    if (settingKey is YamlScalarNode { Value: "path" })
                                    {
                                        text.Items(target, setting, spans);
                                    }
                                }

                                break;
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>The value of <paramref name="key"/> in a mapping, or null.</summary>
    private static YamlNode? Value(YamlMappingNode mapping, string key)
        => mapping.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode s && s.Value == key).Value;

    private static bool IsComment(string line) => line.TrimStart().StartsWith('#');

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    /// <summary>A place in the document: its line and column, from 1.</summary>
    private readonly record struct Place(int Line, int Column)
    {
        public static Place Of(Mark mark) => new((int)mark.Line, (int)mark.Column);

        public static Place Later(Place a, Place b) => a.Line > b.Line || (a.Line == b.Line && a.Column >= b.Column) ? a : b;
    }

    /// <summary>
    /// The document's lines, which settle where a node's text ends: the parser marks the end of a block where the next token
    /// starts, which can be lines further down, and the end of a flow collection (<c>[a, b]</c>) at its opening bracket.
    /// </summary>
    internal sealed class Text(IReadOnlyList<string> lines)
    {
        public IReadOnlyList<string> Lines { get; } = lines;

        /// <summary>A key and its value, from the key to where the value ends.</summary>
        public DimensionYamlSpan Entry(string target, YamlNode key, YamlNode value) => Span(target, Place.Of(key.Start), Place.Later(End(key), End(value)));

        /// <summary>Each item of a list, by its place; a single value written where a list may be is the list's one item.</summary>
        public void Items(string target, YamlNode value, List<DimensionYamlSpan> spans)
        {
            switch (value)
            {
                case YamlScalarNode scalar:
                    spans.Add(Span(target + ".0", Place.Of(scalar.Start), End(scalar)));
                    break;
                case YamlSequenceNode sequence:
                    var index = 0;
                    foreach (var child in sequence.Children)
                    {
                        spans.Add(Span($"{target}.{index}", Place.Of(child.Start), End(child)));
                        index++;
                    }

                    break;
            }
        }

        /// <summary>Each entry of a mapping, by its key.</summary>
        public void Entries(string target, YamlMappingNode mapping, List<DimensionYamlSpan> spans)
        {
            foreach (var (key, value) in mapping.Children)
            {
                if (key is YamlScalarNode { Value: { } name })
                {
                    spans.Add(Entry($"{target}.{name}", key, value));
                }
            }
        }

        private static DimensionYamlSpan Span(string target, Place start, Place end) => new(target, start.Line, start.Column, end.Line, end.Column);

        /// <summary>Where the text of <paramref name="node"/> ends.</summary>
        private Place End(YamlNode node)
        {
            switch (node)
            {
                case YamlSequenceNode { Style: SequenceStyle.Flow } flow:
                    return Closing(flow.Children.Count == 0 ? After(Place.Of(flow.Start)) : flow.Children.Select(End).Aggregate(Place.Later), ']');
                case YamlMappingNode { Style: MappingStyle.Flow } flow:
                    return Closing(flow.Children.Count == 0 ? After(Place.Of(flow.Start)) : flow.Children.SelectMany(kv => new[] { End(kv.Key), End(kv.Value) }).Aggregate(Place.Later), '}');
                case YamlSequenceNode { Children.Count: > 0 } sequence:
                    return sequence.Children.Select(End).Aggregate(Place.Later);
                case YamlMappingNode { Children.Count: > 0 } mapping:
                    return mapping.Children.SelectMany(kv => new[] { End(kv.Key), End(kv.Value) }).Aggregate(Place.Later);
                default:
                    // A block scalar (description: >) is marked as ending where the next line starts; its text ends on the line before.
                    var start = Place.Of(node.Start);
                    var end = Place.Of(node.End);
                    return end.Column <= 1 && end.Line > start.Line && end.Line - 2 < Lines.Count
                        ? new Place(end.Line - 1, Lines[end.Line - 2].Length + 1)
                        : end;
            }
        }

        /// <summary>The place after <paramref name="at"/>, on its line.</summary>
        private static Place After(Place at) => at with { Column = at.Column + 1 };

        /// <summary>
        /// The place after the bracket that closes a flow collection, found from where its last item ends past spaces, commas and
        /// line breaks; where the text holds no such bracket, where its last item ends.
        /// </summary>
        private Place Closing(Place from, char bracket)
        {
            var line = from.Line;
            var column = from.Column;
            while (line <= Lines.Count)
            {
                var text = Lines[line - 1];
                for (var at = column - 1; at < text.Length; at++)
                {
                    var c = text[at];
                    if (c == bracket)
                    {
                        return new Place(line, at + 2);
                    }

                    if (c is not (' ' or '\t' or ','))
                    {
                        return from;
                    }
                }

                line++;
                column = 1;
            }

            return from;
        }
    }
}
