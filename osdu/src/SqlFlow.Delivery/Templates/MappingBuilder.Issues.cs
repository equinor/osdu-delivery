using System.Text.RegularExpressions;
using SqlFlow.Delivery.Documents;

namespace SqlFlow.Delivery.Templates;

// What a draft still lacks before it can be written as a mapping that loads (osdu/docs/reference/concepts/gui-build.md,
// Mapping builder): the draft checked entry by entry, with the lookups its value nodes read and its
// $restrictWhenMissing entries. What one value, list, lookup, assertion or modifier lacks is checked in a file of its
// own beside this one.
public static partial class MappingBuilder
{
    /// <summary>What the draft still lacks before it can be written as a mapping that loads, entry by entry.</summary>
    public static IReadOnlyList<MappingDraftIssue> Incomplete(MappingDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var issues = new List<MappingDraftIssue>();
        void Error(string message, string? target = null) => issues.Add(new MappingDraftIssue(MappingDraftIssue.ErrorSeverity, message, target));

        if (!FileNamePart().IsMatch(draft.Name))
        {
            Error("Give the mapping a name of letters, digits, underscore, hyphen and dot; it names the file as Name@version.yaml.");
        }

        if (!FileNamePart().IsMatch(draft.Version))
        {
            Error("Give the mapping a version of letters, digits, underscore, hyphen and dot, such as 1.0.0.");
        }

        if (string.IsNullOrWhiteSpace(draft.TemplateKind) || string.IsNullOrWhiteSpace(draft.TemplateVersion))
        {
            Error("Pick the template the mapping fills, among those the repository's template folders hold.");
        }

        if (string.IsNullOrWhiteSpace(draft.System))
        {
            Error("Name the source system; it is part of every record's key.");
        }

        if (draft.Key.Count == 0 || draft.Key.Any(k => !ColumnName().IsMatch(k)))
        {
            Error("Name the dataset columns that identify a record, such as log_id.");
        }

        if (draft.Identity.Any(k => !ColumnName().IsMatch(k)))
        {
            Error("Name each dataset column an operator finds a record by, such as wellbore_uwi.");
        }

        if (draft.IdFrom is { } idFrom && idFrom != MappingMapper.IdFromDeliveryKey && idFrom != MappingMapper.IdFromKey)
        {
            Error($"Make the OSDU id from the delivery key ({MappingMapper.IdFromDeliveryKey}) or from the key's values ({MappingMapper.IdFromKey}).");
        }

        if (!string.IsNullOrWhiteSpace(draft.Label))
        {
            foreach (Match token in LabelTokenPattern().Matches(draft.Label))
            {
                var column = token.Groups["token"].Value.Trim();
                if (!ColumnName().IsMatch(column) && !(column.StartsWith(DatasetReference + ".", StringComparison.Ordinal) && ColumnName().IsMatch(column[(DatasetReference.Length + 1)..])))
                {
                    Error($"The label's {token.Value} names no column; a label token is a column of the dataset's own row, such as {{wellbore_uwi}}.");
                }
            }
        }

        foreach (var search in draft.Searches)
        {
            if (!ColumnName().IsMatch(search.Name ?? string.Empty))
            {
                Error($"Name each search with letters, digits, underscores and hyphens; entries read it as {MappingMapper.SearchKey}: <name>.");
            }

            if (string.IsNullOrWhiteSpace(search.Kind))
            {
                Error($"Search '{search.Name}': give the kind it looks in, such as osdu:wks:master-data--Wellbore:*.");
            }

            if (string.IsNullOrWhiteSpace(search.SchemaKind) || string.IsNullOrWhiteSpace(search.SchemaVersion))
            {
                Error($"Search '{search.Name}': pick the template whose schema says how the kind it searches is indexed, among those the repository's template folders hold.");
            }
        }

        var lookupNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lookup in draft.Lookups)
        {
            LookupIssues(lookup, lookupNames, Error);
        }

        var layout = Layout(draft);
        foreach (var left in layout.LeftOut.Where(l => l.Reason is not null))
        {
            Error(left.Reason!, left.Target);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entry, index) in draft.Entries.Select((entry, index) => (entry, index)))
        {
            var target = entry.Target.Trim();
            if (!TemplatePath.TryParse(target, out var parsed, out var targetError))
            {
                Error(char.ToUpperInvariant(targetError![0]) + targetError[1..] + ".", target);
                continue;
            }

            // One variable, however its target is written: osdu.tags['Source'] is osdu.tags.Source.
            if (!seen.Add(parsed!.Text))
            {
                Error($"{target} has more than one entry.", target);
            }

            var scope = layout.Scopes.GetValueOrDefault(index);
            switch (entry.Input)
            {
                case MappingDraftInput.Repeat when !ColumnName().IsMatch(entry.Child ?? string.Empty):
                    Error($"{target}: choose the child dataset whose rows become the items.", target);
                    break;
                case MappingDraftInput.Repeat when layout.EmptyRepeats.Contains(index):
                    Error($"{target}: add an entry for each property an item takes from its row, such as {target}[].Name.", target);
                    break;
                case MappingDraftInput.Repeat:
                    break;
                case MappingDraftInput.Coalesce:
                    CoalesceIssues(draft, entry, target, scope, Error);
                    break;
                case MappingDraftInput.List:
                    ListIssues(draft, entry, target, scope, Error);
                    break;
                case MappingDraftInput.Group:
                    Error($"{target}: an object input is an item of a list of objects; choose a list input and add the object as one of its items.", target);
                    break;
                default:
                    ValueIssues(draft, entry, target, target, scope, Error);
                    break;
            }

            foreach (var problem in AssertionProblems(entry))
            {
                Error($"{target}: {problem}", target);
            }

            // A repeat's own condition decides for the whole array, so it reads the row the array is in; its $where reads each child row.
            if (!string.IsNullOrWhiteSpace(entry.When)
                && MappingMapper.ReadCondition(entry.When.Trim(), MappingMapper.WhenKey, entry.Input == MappingDraftInput.Repeat ? null : scope, out var whenProblem) is null)
            {
                Error($"{target}: {whenProblem}", target);
            }

            if (!string.IsNullOrWhiteSpace(entry.Where))
            {
                if (entry.Input != MappingDraftInput.Repeat)
                {
                    Error($"{target}: {MappingMapper.WhereKey} keeps the child rows a repeat makes items of; only a repeat input takes it.", target);
                }
                else if (ColumnName().IsMatch(entry.Child?.Trim() ?? string.Empty)
                    && MappingMapper.ReadCondition(entry.Where.Trim(), MappingMapper.WhereKey, entry.Child!.Trim(), out var whereProblem) is null)
                {
                    Error($"{target}: {whereProblem}", target);
                }
            }
        }

        // The loader refuses a lookup nothing reads, since it says something no record uses.
        var read = draft.Entries.SelectMany(ValueNodesOf).SelectMany(LookupsRead).ToHashSet(StringComparer.Ordinal);
        foreach (var name in draft.Lookups.Select(l => l.Name?.Trim() ?? string.Empty).Where(n => ColumnName().IsMatch(n) && !read.Contains(n)).Distinct(StringComparer.Ordinal))
        {
            Error($"Lookup '{name}' is read by no entry; read a field of its record with a lookup input, or in a find all line, or remove it.", LookupTarget(name));
        }

        // A record restricted while a value is missing goes out under the access the mapping names, so the loader refuses either
        // without the other (osdu/docs/reference/flow/mapping-restricted-access.md), and the draft says so before it is written.
        RestrictionIssues(draft, Error);
        return issues;
    }

    /// <summary>
    /// What the draft's <c>$restrictWhenMissing</c> entries and its <c>restrictedAccess</c> lack, as the loader would refuse them:
    /// a restricting entry is a required one, never a repeat, an alternative or an object item, and the mapping names the access
    /// its restricted records go out under exactly when an entry restricts.
    /// </summary>
    private static void RestrictionIssues(MappingDraft draft, Action<string, string?> error)
    {
        foreach (var entry in draft.Entries)
        {
            var target = entry.Target.Trim();
            if (entry.RestrictWhenMissing && entry.Input == MappingDraftInput.Repeat)
            {
                error($"{target}: {MappingMapper.RestrictWhenMissingKey} restricts the record when a value is missing, and a repeat writes an array of rows; mark a property of its items instead.", target);
            }

            foreach (var alternative in entry.Alternatives.Where(a => a.RestrictWhenMissing))
            {
                error($"{target}: {MappingMapper.RestrictWhenMissingKey} decides for the whole coalesce entry; mark the entry, not one of its alternatives.", target);
            }
        }

        var restricting = draft.Entries.SelectMany(e => e.Alternatives.Count > 0 ? [e] : ValueNodesOf(e)).Where(e => e.RestrictWhenMissing).ToList();
        foreach (var entry in restricting.Where(e => !e.Required))
        {
            error(
                $"{entry.Target.Trim()}: {MappingMapper.RestrictWhenMissingKey} restricts the record when the value is missing, and an entry that is not required leaves the property out with the record's own access; keep one of them.",
                entry.Target.Trim());
        }

        if (restricting.Count > 0 && string.IsNullOrWhiteSpace(draft.RestrictedAccess))
        {
            error(
                $"{restricting[0].Target.Trim()} restricts the record when its value is missing; name the access restriction, registered for the partition the flow delivers to, that such a record goes out under ({MappingMapper.RestrictedAccessKey}).",
                restricting[0].Target.Trim());
        }

        if (restricting.Count == 0 && !string.IsNullOrWhiteSpace(draft.RestrictedAccess))
        {
            error($"{MappingMapper.RestrictedAccessKey} names the access a record goes out under while a value an entry marks {MappingMapper.RestrictWhenMissingKey} is missing, and no entry is marked so; mark one, or clear it.", null);
        }
    }

    /// <summary>
    /// An entry and the value nodes it reads through: a coalesce entry's alternatives, a list's items, and the properties of
    /// an item of a list of objects with what they read through in turn.
    /// </summary>
    private static IEnumerable<MappingDraftEntry> ValueNodesOf(MappingDraftEntry entry)
        => [entry, .. entry.Alternatives, .. entry.Items.SelectMany(ValueNodesOf), .. entry.Properties.SelectMany(ValueNodesOf)];

    /// <summary>The lookups one value node reads: the one a lookup input reads, and the one a find all line keys by.</summary>
    private static IEnumerable<string> LookupsRead(MappingDraftEntry node)
    {
        if (node.Input == MappingDraftInput.Lookup && !string.IsNullOrWhiteSpace(node.Lookup))
        {
            yield return node.Lookup.Trim();
        }

        if (node.Input == MappingDraftInput.Cache && node.FindAll?.Lookup is { } path && !string.IsNullOrWhiteSpace(path))
        {
            yield return path.Trim().Split('.')[0];
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]+$")]
    private static partial Regex FileNamePart();

    [GeneratedRegex(@"\{(?<token>[^{}]*)\}")]
    private static partial Regex LabelTokenPattern();
}
