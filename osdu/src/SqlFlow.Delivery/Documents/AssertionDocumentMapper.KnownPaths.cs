using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

// The full paths of known values (osdu/docs/reference/flow/known-values.md): a path in the OSDU record from its
// root, the entries of a list of paths, and the steps a path takes to the records naming one.
internal static partial class AssertionMapper
{
    /// <summary>
    /// A full path of known values. A path in the OSDU record, from its root (<c>data.Name</c>, <c>tags.SourceProject</c>), as
    /// the assertion's field, a mapping's assertions and a dimension read a record; or a list of paths, read step by step as
    /// a dimension's label and attributes are (osdu/docs/reference/flow/dimension-attributes.md): each path but
    /// the last reads the reference the next records are found by, and the last reads the value
    /// (<c>[data.WellboreID, data.FacilityName]</c>, the name of the wellbore a log belongs to). An entry
    /// <c>group--Entity[reference]</c> steps the other way, to the records of that type whose reference names the record
    /// reached so far (<c>[work-product-component--WellLog[data.WellboreID], data.Name]</c>, the names of a wellbore's
    /// logs). A path names properties: the item of a list a row reads is the one its locating values find, never one by
    /// its position.
    /// </summary>
    private static KnownColumn ReadKnownPath(string written, string at, TestContext test)
    {
        var text = written.Trim();
        if (!text.StartsWith('['))
        {
            if (ChildrenPrefix().IsMatch(text))
            {
                throw new FlowValidationException(
                    $"{test.Source}: {at} '{Shown(text)}' steps to the records naming one outside a list of paths; write the step and the path read in them as a list, such as '[work-product-component--WellLog[data.WellboreID], data.Name]'.");
            }

            var path = PathOf(text, written, at, test);
            return new KnownColumn { Path = path, Steps = [new KnownStep(KnownStepVia.Record, path)] };
        }

        var entries = StepEntries(text, written, at, test);
        if (entries.Count > KnownColumn.MaxSteps)
        {
            throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture,
                $"{test.Source}: {at} lists {entries.Count} paths; a list reads through at most {KnownColumn.MaxSteps}: the record's own and three more to the records it names or that name it."));
        }

        var steps = new List<KnownStep>();
        var afterChildren = false;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (ChildrenStep().Match(entry) is { Success: true } children)
            {
                if (i == entries.Count - 1)
                {
                    throw new FlowValidationException(
                        $"{test.Source}: {at} ends at the {children.Groups["type"].Value} records naming the record, and reads nothing of them; end the list with the path read in them, such as '[{entry}, data.Name]'.");
                }

                if (i > 0 && !afterChildren)
                {
                    // The record the reference before names, whose children the step reaches.
                    steps.Add(new KnownStep(KnownStepVia.Parent, null));
                }

                steps.Add(new KnownStep(KnownStepVia.Children, null, children.Groups["type"].Value, PathOf(children.Groups["reference"].Value, written, at, test)));
                afterChildren = true;
                continue;
            }

            var path = PathOf(entry, written, at, test);
            if (afterChildren)
            {
                // The path read in the records the step before reached.
                steps[^1] = steps[^1] with { Field = path };
                afterChildren = false;
                continue;
            }

            steps.Add(new KnownStep(i == 0 ? KnownStepVia.Record : KnownStepVia.Parent, path));
        }

        // A list of one path is that path, so it is written, compared and reported as the path itself.
        return new KnownColumn { Path = entries.Count == 1 ? steps[0].Field! : "[" + string.Join(", ", entries) + "]", Steps = steps };
    }

    /// <summary>
    /// The entries of a list of paths as a key writes it, <c>[data.WellboreID, data.FacilityName]</c>: separated by commas
    /// outside brackets, each optionally quoted as the same list in a dimension is; one entry is the path itself.
    /// </summary>
    private static List<string> StepEntries(string text, string written, string at, TestContext test)
    {
        if (!text.EndsWith(']'))
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} '{Shown(written)}' opens a list of paths it does not close; write it as '[data.WellboreID, data.FacilityName]'.");
        }

        var entries = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        char? quote = null;
        foreach (var c in text[1..^1])
        {
            if (quote is { } open)
            {
                if (c == open)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"' when current.ToString().Trim().Length == 0:
                    quote = c;
                    continue;
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    entries.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
            }

            if (depth < 0)
            {
                break;
            }

            current.Append(c);
        }

        entries.Add(current.ToString().Trim());
        if (depth != 0 || quote is not null)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} '{Shown(written)}' has a bracket or a quote that does not pair up; write the list as '[data.WellboreID, data.FacilityName]'.");
        }

        if (entries.Any(e => e.Length == 0))
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} '{Shown(written)}' has an empty entry; write a path between each two commas.");
        }

        return entries;
    }

    /// <summary>A path a step reads: a record path from a root property, with no item by its position, its [*] steps implied.</summary>
    private static string PathOf(string part, string written, string at, TestContext test)
    {
        var path = ConditionReader.RecordPath(part, at, test.Source);
        if (PositionStep().IsMatch(path))
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} '{Shown(written)}' names an item of a list by its position; a row reads the item its locating values find, so name the property ({PositionStep().Replace(path, string.Empty)}).");
        }

        return path.Replace("[*]", string.Empty, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\[\d+\]", RegexOptions.CultureInvariant)]
    private static partial Regex PositionStep();

    /// <summary>An entry stepping to the records naming one: <c>group--Entity[reference]</c>.</summary>
    [GeneratedRegex(@"^(?<type>[A-Za-z0-9_.-]+--[A-Za-z0-9_.]+)\[(?<reference>[^\]]+)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex ChildrenStep();

    /// <summary>The start of such an entry, as a path that is not a list may begin with one by mistake.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.-]+--[A-Za-z0-9_.]+\[", RegexOptions.CultureInvariant)]
    private static partial Regex ChildrenPrefix();
}
