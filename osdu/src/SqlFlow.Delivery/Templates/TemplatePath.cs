using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Templates;

/// <summary>One step of a template path: a property name, and whether the path steps into the items of the array it names.</summary>
public readonly record struct TemplatePathSegment(string Name, bool IntoArray)
{
    /// <summary>The step as a path writes it after the one before, without the dot before a bare name: <c>Curves[]</c>, <c>['lra.dataID']</c>.</summary>
    public override string ToString() => RecordPathNotation.Name(Name) + (IntoArray ? "[]" : string.Empty);
}

/// <summary>
/// The address of a template variable: <c>osdu.</c> followed by the property path in the OSDU record, with <c>[]</c>
/// marking the step into an array of objects (<c>osdu.data.Curves[].CurveID</c>). A path steps into at most one array:
/// a repeater inside a repeated item is not supported. A property whose name holds a dot, a bracket or a space is one step,
/// written in brackets and quotes (<c>osdu.tags['lra.securityClassification']</c>, <see cref="RecordPathNotation"/>).
/// </summary>
public sealed class TemplatePath : IEquatable<TemplatePath>
{
    /// <summary>The first word of every template path.</summary>
    public const string Prefix = "osdu";

    private TemplatePath(IReadOnlyList<TemplatePathSegment> segments)
    {
        Segments = segments;
        RecordPath = Write(segments, withItems: true);
        Text = TextOf(RecordPath);
        SchemaPath = Write(segments, withItems: false);
    }

    public IReadOnlyList<TemplatePathSegment> Segments { get; }

    /// <summary>The path as a mapping writes it: <c>osdu.data.Curves[].CurveID</c>.</summary>
    public string Text { get; }

    /// <summary>The path from the record root, steps into arrays marked: <c>data.Curves[].CurveID</c>, as a check of a record names a place.</summary>
    public string RecordPath { get; }

    /// <summary>The path from the record root the schema resolves, arrays stepped into implicitly: <c>data.Curves.CurveID</c>.</summary>
    public string SchemaPath { get; }

    /// <summary>The first property name after <c>osdu.</c>: <c>data</c>, <c>acl</c>, <c>legal</c>, <c>tags</c>.</summary>
    public string Root => Segments[0].Name;

    /// <summary>True when the path steps into an array of objects.</summary>
    public bool IsRepeated => Segments.Any(s => s.IntoArray);

    /// <summary>The array a repeated path steps into (<c>osdu.data.Curves</c> for <c>osdu.data.Curves[].CurveID</c>), or null.</summary>
    public TemplatePath? Repeater
    {
        get
        {
            var index = IndexOfArrayStep();
            return index < 0
                ? null
                : new TemplatePath(Segments.Take(index + 1).Select((s, i) => i == index ? s with { IntoArray = false } : s).ToList());
        }
    }

    /// <summary>The property names inside the repeated item (<c>CurveID</c>), or the whole path when it is not repeated.</summary>
    public IReadOnlyList<string> WithinItem
    {
        get
        {
            var index = IndexOfArrayStep();
            return Segments.Skip(index + 1).Select(s => s.Name).ToList();
        }
    }

    /// <summary>The path one level up, or null at the record root.</summary>
    public TemplatePath? Parent => Segments.Count <= 1
        ? null
        : new TemplatePath(Segments.Take(Segments.Count - 1).Select((s, i) => i == Segments.Count - 2 ? s with { IntoArray = false } : s).ToList());

    /// <summary>The last property name.</summary>
    public string Leaf => Segments[^1].Name;

    /// <summary>A path from segments, which must be valid names; used where the segments come from a schema.</summary>
    public static TemplatePath Of(IReadOnlyList<TemplatePathSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            throw new ArgumentException("A template path needs at least one property name.", nameof(segments));
        }

        return new TemplatePath(segments.ToList());
    }

    /// <summary>
    /// A path from the steps a mapping lays out, or why it is not one: every name holds something, the path steps into one
    /// array at most, and never ends at the items of one.
    /// </summary>
    public static bool TryCreate(IReadOnlyList<TemplatePathSegment> segments, out TemplatePath? path, out string? error)
    {
        ArgumentNullException.ThrowIfNull(segments);
        path = null;
        error = null;
        if (segments.Count == 0)
        {
            error = "a target names at least one property, such as osdu.data.Name";
            return false;
        }

        if (segments.Any(s => string.IsNullOrEmpty(s.Name)))
        {
            error = "a target names no property with an empty name";
            return false;
        }

        var shown = TextOf(Write(segments, withItems: true));
        if (segments.Count(s => s.IntoArray) > 1)
        {
            error = $"target '{shown}' steps into more than one array; a repeater inside a repeated item is not supported";
            return false;
        }

        if (segments[^1].IntoArray)
        {
            var array = TextOf(Write([.. segments.Take(segments.Count - 1), segments[^1] with { IntoArray = false }], withItems: true));
            error = $"target '{shown}' names the items of an array; target a property inside them, such as {array}[].Name, or the array itself as {array}";
            return false;
        }

        path = new TemplatePath(segments.ToList());
        return true;
    }

    /// <summary>Parses a path as a mapping writes it, or says why it is not one.</summary>
    public static bool TryParse(string? text, out TemplatePath? path, out string? error)
    {
        path = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "a target is required, such as osdu.data.Name";
            return false;
        }

        var trimmed = text.Trim();
        var rest = trimmed.StartsWith(Prefix + ".", StringComparison.Ordinal)
            ? trimmed[(Prefix.Length + 1)..]
            : trimmed.StartsWith(Prefix + "[", StringComparison.Ordinal) ? trimmed[Prefix.Length..] : null;
        if (rest is null)
        {
            error = $"target '{trimmed}' must start with '{Prefix}.', the template of the OSDU record, such as osdu.data.Name";
            return false;
        }

        if (!RecordPathNotation.TryParse(rest, out var steps, out var why))
        {
            error = $"target '{trimmed}' has an invalid property name: {why}";
            return false;
        }

        var segments = new List<TemplatePathSegment>(steps.Count);
        foreach (var step in steps)
        {
            switch (step.Kind)
            {
                case RecordPathStepKind.Property:
                    segments.Add(new TemplatePathSegment(step.Name!, false));
                    break;
                case RecordPathStepKind.Items when !segments[^1].IntoArray:
                    segments[^1] = segments[^1] with { IntoArray = true };
                    break;
                case RecordPathStepKind.Items:
                    error = $"target '{trimmed}' has an invalid property name: {segments[^1].Name}[][] steps into the items of its items";
                    return false;
                default:
                    error = $"target '{trimmed}' has an invalid property name: {step} names {(step.Kind == RecordPathStepKind.Index ? "one item" : "every item")} of an array, and a target names the items of an array with []";
                    return false;
            }
        }

        return TryCreate(segments, out path, out error);
    }

    /// <summary>
    /// The template path of a path from the record root as a check of a record names it (<c>tags['lra.dataID']</c>):
    /// <c>osdu.</c> before a bare first name, <c>osdu</c> before a quoted one.
    /// </summary>
    public static string TextOf(string recordPath)
    {
        ArgumentNullException.ThrowIfNull(recordPath);
        return recordPath.StartsWith('[') ? Prefix + recordPath : Prefix + "." + recordPath;
    }

    /// <summary>
    /// Whether the variable at <paramref name="inner"/> is the one at <paramref name="outer"/> or lies inside it: a property
    /// of it, or of its items. Both are template paths as <see cref="Text"/> writes them.
    /// </summary>
    public static bool IsWithin(string inner, string outer) => RecordPathNotation.IsWithin(inner, outer);

    public bool Equals(TemplatePath? other) => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is TemplatePath other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    public override string ToString() => Text;

    private static string Write(IReadOnlyList<TemplatePathSegment> segments, bool withItems)
    {
        var path = string.Empty;
        foreach (var segment in segments)
        {
            path = RecordPathNotation.Append(path, segment.Name);
            if (withItems && segment.IntoArray)
            {
                path += "[]";
            }
        }

        return path;
    }

    private int IndexOfArrayStep()
    {
        for (var i = 0; i < Segments.Count; i++)
        {
            if (Segments[i].IntoArray)
            {
                return i;
            }
        }

        return -1;
    }
}
