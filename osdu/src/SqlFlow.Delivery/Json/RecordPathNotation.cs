using System.Globalization;
using System.Text;

namespace SqlFlow.Delivery.Json;

/// <summary>What one step of a record path does (<see cref="RecordPathNotation"/>).</summary>
public enum RecordPathStepKind
{
    /// <summary>Names a property: <c>.Name</c>, or <c>['lra.securityClassification']</c> for a name a path cannot write bare.</summary>
    Property,

    /// <summary>Steps into every item of an array as a template path writes it: <c>[]</c>.</summary>
    Items,

    /// <summary>Steps into every item of an array, or every value of an object, as a record path reads it: <c>[*]</c>.</summary>
    Wildcard,

    /// <summary>Steps into one item of an array: <c>[2]</c>.</summary>
    Index,
}

/// <summary>One step of a record path: a property by its name, or a step into the items of an array.</summary>
/// <param name="Kind">What the step does.</param>
/// <param name="Name">The property's name, exactly as the record holds it, for a <see cref="RecordPathStepKind.Property"/> step.</param>
/// <param name="Index">The item's place, from 0, for an <see cref="RecordPathStepKind.Index"/> step.</param>
public readonly record struct RecordPathStep(RecordPathStepKind Kind, string? Name, int Index)
{
    public static RecordPathStep OfProperty(string name) => new(RecordPathStepKind.Property, name ?? throw new ArgumentNullException(nameof(name)), 0);

    public static RecordPathStep OfItems() => new(RecordPathStepKind.Items, null, 0);

    public static RecordPathStep OfWildcard() => new(RecordPathStepKind.Wildcard, null, 0);

    public static RecordPathStep OfIndex(int index) => new(RecordPathStepKind.Index, null, index);

    /// <summary>The step as a path writes it after the step before, without the dot before a bare name.</summary>
    public override string ToString() => Kind switch
    {
        RecordPathStepKind.Property => RecordPathNotation.Name(Name!),
        RecordPathStepKind.Items => "[]",
        RecordPathStepKind.Wildcard => "[*]",
        _ => string.Create(CultureInfo.InvariantCulture, $"[{Index}]"),
    };
}

/// <summary>
/// How a path into an OSDU record names a property (osdu/docs/reference/flow/mapping-property-names.md): after a dot when the
/// name can be written bare, and in brackets and quotes when it holds a dot, a bracket or a space, as JSONPath writes a
/// name (RFC 9535): <c>data.Name</c>, <c>tags['lra.securityClassification']</c>. Template paths, the places a check of a
/// record names, the fields an assertion reads and the paths the GUI shows all write a name this way, so a name holding a dot
/// is one step wherever a path is read.
/// </summary>
/// <remarks>
/// A name is written bare exactly when the paths have always taken it bare (no dot, bracket or space), so a path holding no
/// other name reads and writes as it always has. A quoted name is written in single quotes with RFC 9535's escapes; reading
/// takes double quotes as well, and a dot before the bracket.
/// </remarks>
public static class RecordPathNotation
{
    /// <summary>NEXT LINE (U+0085), which <c>\s</c> reads as a space and no Unicode space category holds.</summary>
    private const char NextLine = (char)0x85;

    /// <summary>
    /// Whether <paramref name="name"/> is written bare in a path: it is not empty, is not <c>*</c> (which a path reads as every
    /// item), and holds no dot, no bracket and no space.
    /// </summary>
    public static bool IsPlain(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name == "*")
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c is '.' or '[' or ']' || IsSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The name as a path writes it as a step: bare when it can be, else quoted in brackets.</summary>
    public static string Name(string name) => IsPlain(name) ? name : Quote(name);

    /// <summary>
    /// <paramref name="name"/> in brackets and single quotes, as RFC 9535 writes a name in a normalized path: a quote and a
    /// backslash escaped with a backslash, and a control character as its escape (<c>\n</c>, <c>\u0001</c>).
    /// </summary>
    public static string Quote(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var text = new StringBuilder(name.Length + 4).Append("['");
        foreach (var c in name)
        {
            switch (c)
            {
                case '\\':
                    text.Append(@"\\");
                    break;
                case '\'':
                    text.Append(@"\'");
                    break;
                case '\b':
                    text.Append(@"\b");
                    break;
                case '\f':
                    text.Append(@"\f");
                    break;
                case '\n':
                    text.Append(@"\n");
                    break;
                case '\r':
                    text.Append(@"\r");
                    break;
                case '\t':
                    text.Append(@"\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        text.Append(@"\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        text.Append(c);
                    }

                    break;
            }
        }

        return text.Append("']").ToString();
    }

    /// <summary>The path of property <paramref name="name"/> of what <paramref name="path"/> names; the empty path is the record.</summary>
    public static string Append(string path, string name)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(name);
        if (!IsPlain(name))
        {
            return path + Quote(name);
        }

        return path.Length == 0 ? name : path + "." + name;
    }

    /// <summary>The steps written as one path, each name bare where it can be.</summary>
    public static string Write(IEnumerable<RecordPathStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var path = string.Empty;
        foreach (var step in steps)
        {
            path = step.Kind == RecordPathStepKind.Property ? Append(path, step.Name!) : path + step;
        }

        return path;
    }

    /// <summary>
    /// Reads <paramref name="path"/> into its steps, or says why it is not a path: names after dots or in quotes in brackets,
    /// and the steps into arrays, <c>[]</c>, <c>[*]</c> and <c>[n]</c>. A bare <c>*</c> reads as <c>[*]</c>.
    /// </summary>
    /// <returns>True with the steps, or false with a sentence saying what is wrong, naming the part of the path it is about.</returns>
    public static bool TryParse(string? path, out IReadOnlyList<RecordPathStep> steps, out string? error)
    {
        steps = [];
        error = null;
        if (string.IsNullOrEmpty(path))
        {
            error = "the path is empty";
            return false;
        }

        var read = new List<RecordPathStep>();
        var afterDot = false;
        var i = 0;
        while (i < path.Length)
        {
            var c = path[i];
            if (c == '.')
            {
                if (read.Count == 0 || afterDot)
                {
                    error = "it holds an empty property name: a dot follows nothing, or another dot";
                    return false;
                }

                afterDot = true;
                i++;
                continue;
            }

            if (c == ']')
            {
                error = "a ']' closes no '['";
                return false;
            }

            if (c == '[')
            {
                var open = SkipBlanks(path, i + 1);
                if (open < path.Length && path[open] is '\'' or '"')
                {
                    if (!TryReadQuoted(path, open, out var name, out var end, out error))
                    {
                        return false;
                    }

                    var close = SkipBlanks(path, end);
                    if (close >= path.Length || path[close] != ']')
                    {
                        error = $"the quoted name '{name}' is not closed with ']'";
                        return false;
                    }

                    if (name.Length == 0)
                    {
                        error = "it holds an empty property name: [''] names nothing";
                        return false;
                    }

                    read.Add(RecordPathStep.OfProperty(name));
                    afterDot = false;
                    i = close + 1;
                    continue;
                }

                var shut = path.IndexOf(']', i + 1);
                if (shut < 0)
                {
                    error = "a '[' is not closed with ']'";
                    return false;
                }

                var inner = path[(i + 1)..shut].Trim();
                if (read.Count == 0)
                {
                    error = $"it starts with [{inner}], and a step into an array follows the array it steps into";
                    return false;
                }

                if (inner.Length == 0)
                {
                    read.Add(RecordPathStep.OfItems());
                }
                else if (inner == "*")
                {
                    read.Add(RecordPathStep.OfWildcard());
                }
                else if (inner.All(char.IsAsciiDigit) && int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    read.Add(RecordPathStep.OfIndex(index));
                }
                else
                {
                    error = $"[{inner}] is not a step: a name in brackets is written in quotes, ['{inner}'], and an item by its place, such as [0]";
                    return false;
                }

                afterDot = false;
                i = shut + 1;
                continue;
            }

            var start = i;
            while (i < path.Length && path[i] is not ('.' or '[' or ']'))
            {
                i++;
            }

            var bare = path[start..i];
            if (read.Count > 0 && !afterDot)
            {
                error = $"'{bare}' follows '{read[^1]}' without a dot";
                return false;
            }

            if (bare == "*")
            {
                read.Add(RecordPathStep.OfWildcard());
            }
            else if (!IsPlain(bare))
            {
                error = $"'{bare}' holds a space, and a name holding one is written in quotes, {Quote(bare)}";
                return false;
            }
            else
            {
                read.Add(RecordPathStep.OfProperty(bare));
            }

            afterDot = false;
        }

        if (afterDot)
        {
            error = "it ends with a dot";
            return false;
        }

        steps = read;
        return true;
    }

    /// <summary>
    /// The property names of a path from the record's root as a schema is looked up by, arrays stepped into implicitly
    /// (<c>data.Curves.CurveID</c>, <c>tags['lra.dataID']</c>): the dotted parts, empty ones skipped, with each quoted name read
    /// whole. Anything else in brackets stays part of the name it follows, as a schema lookup has always read it. Null when a
    /// quoted name is not closed.
    /// </summary>
    public static IReadOnlyList<string>? Names(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.Contains("['", StringComparison.Ordinal) && !path.Contains("[\"", StringComparison.Ordinal))
        {
            return path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        }

        var names = new List<string>();
        var part = new StringBuilder();
        var i = 0;
        while (i < path.Length)
        {
            var c = path[i];
            if (c == '[' && i + 1 < path.Length && path[i + 1] is '\'' or '"')
            {
                Flush(part, names);
                if (!TryReadQuoted(path, i + 1, out var name, out var end, out _) || end >= path.Length || path[end] != ']')
                {
                    return null;
                }

                names.Add(name);
                i = end + 1;
                continue;
            }

            if (c == '.')
            {
                Flush(part, names);
            }
            else
            {
                part.Append(c);
            }

            i++;
        }

        Flush(part, names);
        return names;
    }

    /// <summary>
    /// Whether <paramref name="inner"/> is <paramref name="outer"/> or lies inside it: a property of it, a quoted one among
    /// them, or anything in the items of it. Both are paths as this notation writes them.
    /// </summary>
    public static bool IsWithin(string inner, string outer)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(outer);
        return string.Equals(inner, outer, StringComparison.Ordinal)
            || (inner.Length > outer.Length
                && inner.StartsWith(outer, StringComparison.Ordinal)
                && inner[outer.Length] is '.' or '[');
    }

    /// <summary>
    /// The rest of <paramref name="path"/> below <paramref name="outer"/> (<c>CurveID</c> of <c>data.Curves.CurveID</c> below
    /// <c>data.Curves</c>, <c>['a.b']</c> of <c>data.X['a.b']</c> below <c>data.X</c>), or null when it does not lie strictly
    /// inside it.
    /// </summary>
    public static string? Below(string path, string outer)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(outer);
        if (path.Length <= outer.Length || !IsWithin(path, outer))
        {
            return null;
        }

        return path[outer.Length] == '.' ? path[(outer.Length + 1)..] : path[outer.Length..];
    }

    /// <summary>
    /// The field a search names for a record path: its property names joined by dots, the steps into arrays left out, as the
    /// platform's index names a value (<c>tags.lra.securityClassification</c> for <c>tags['lra.securityClassification']</c>,
    /// whose flattened index takes a dotted tag name and a nested one alike). Null for what is not a path.
    /// </summary>
    public static string? SearchName(string path)
        => TryParse(path, out var steps, out _)
            ? string.Join('.', steps.Where(s => s.Kind == RecordPathStepKind.Property).Select(s => s.Name))
            : null;

    /// <summary>
    /// The path of the object holding the last property <paramref name="path"/> names, every step after that property left
    /// out with it (<c>data.Curves[]</c> for <c>data.Curves[].Unit</c>, <c>data</c> for <c>data.Curves[]</c>); empty for a
    /// property of the record itself, or for what is not a path.
    /// </summary>
    public static string HolderOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!TryParse(path, out var steps, out _))
        {
            return string.Empty;
        }

        var last = LastProperty(steps);
        return last <= 0 ? string.Empty : Write(steps.Take(last));
    }

    /// <summary>
    /// The last property <paramref name="path"/> names, as a reader is shown it: its name as the record holds it, then any
    /// step into its items (<c>Curves[2]</c>, <c>lra.dataID</c>). The path itself when it is not one.
    /// </summary>
    public static string LastName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!TryParse(path, out var steps, out _))
        {
            return path;
        }

        var last = LastProperty(steps);
        return last < 0 ? path : steps[last].Name + string.Concat(steps.Skip(last + 1).Select(s => s.ToString()));
    }

    /// <summary>
    /// Reads the quoted name that starts at <paramref name="start"/> (a quote): RFC 9535's escapes, a backslash before a
    /// quote, a backslash, <c>/</c>, <c>b</c>, <c>f</c>, <c>n</c>, <c>r</c>, <c>t</c> or <c>uXXXX</c>, and either quote inside
    /// the other.
    /// </summary>
    /// <param name="path">The text the name is read from.</param>
    /// <param name="start">The place of the opening quote, single or double.</param>
    /// <param name="name">The name read, its escapes undone; empty when it cannot be read.</param>
    /// <param name="end">The place just after the closing quote.</param>
    /// <param name="error">Why the name cannot be read, or null.</param>
    internal static bool TryReadQuoted(string path, int start, out string name, out int end, out string? error)
    {
        var quote = path[start];
        var text = new StringBuilder();
        var i = start + 1;
        while (i < path.Length)
        {
            var c = path[i];
            if (c == quote)
            {
                name = text.ToString();
                end = i + 1;
                error = null;
                return true;
            }

            if (c < ' ')
            {
                (name, end, error) = (string.Empty, i, $"the quoted name '{text}' holds a control character, which is written as its escape, such as \\n or \\u0001");
                return false;
            }

            if (c != '\\')
            {
                text.Append(c);
                i++;
                continue;
            }

            if (i + 1 >= path.Length)
            {
                break;
            }

            var escaped = path[i + 1];
            switch (escaped)
            {
                case '\\' or '\'' or '"' or '/':
                    text.Append(escaped);
                    break;
                case 'b':
                    text.Append('\b');
                    break;
                case 'f':
                    text.Append('\f');
                    break;
                case 'n':
                    text.Append('\n');
                    break;
                case 'r':
                    text.Append('\r');
                    break;
                case 't':
                    text.Append('\t');
                    break;
                case 'u' when i + 5 < path.Length
                    && int.TryParse(path.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code):
                    text.Append((char)code);
                    i += 6;
                    continue;
                default:
                    (name, end, error) = (string.Empty, i, $"\\{escaped} in the quoted name '{text}' is not an escape: a quoted name escapes a quote, a backslash, /, b, f, n, r, t and uXXXX");
                    return false;
            }

            i += 2;
        }

        (name, end, error) = (string.Empty, path.Length, $"the quoted name '{text}' is not closed with {quote}");
        return false;
    }

    /// <summary>The place of the last property step, or -1 when the steps name none.</summary>
    private static int LastProperty(IReadOnlyList<RecordPathStep> steps)
    {
        for (var i = steps.Count - 1; i >= 0; i--)
        {
            if (steps[i].Kind == RecordPathStepKind.Property)
            {
                return i;
            }
        }

        return -1;
    }

    private static void Flush(StringBuilder part, List<string> names)
    {
        if (part.Length > 0)
        {
            names.Add(part.ToString());
            part.Clear();
        }
    }

    private static int SkipBlanks(string path, int at)
    {
        while (at < path.Length && path[at] is ' ' or '\t')
        {
            at++;
        }

        return at;
    }

    /// <summary>
    /// A space as the regular expression class <c>\s</c> reads one, which a bare name has never held: a check of a record
    /// asks it of every character of every name it walks, so a character below 128 is answered without a category lookup.
    /// </summary>
    private static bool IsSpace(char c)
        => c < 0x80
            ? c is ' ' or '\f' or '\n' or '\r' or '\t' or '\v'
            : c == NextLine
                || char.GetUnicodeCategory(c) is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}
