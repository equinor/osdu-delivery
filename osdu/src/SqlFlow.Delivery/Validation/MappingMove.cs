using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// Where a mapping stands against a newer version of the kind it fills, declared in the order a report lists mappings:
/// what needs a look first.
/// </summary>
public enum MappingMoveState
{
    /// <summary>Something it writes changed in a way that breaks it, or the newer version requires something it leaves empty.</summary>
    NeedsChanges,

    /// <summary>The mapping, or the template it pins, could not be read, so nothing could be set against the newer version.</summary>
    Unchecked,

    /// <summary>Nothing it writes changed in a way that breaks it, and the newer version requires nothing of it that the version it pins did not.</summary>
    Ready,

    /// <summary>The template it pins is the newer version, or holds the same variables: there is nothing to move.</summary>
    Current,
}

/// <summary>
/// One thing a mapping has to change before it moves to a newer version of its kind: a variable it writes that changed in a
/// way that breaks it (<paramref name="Change"/>, with the fields that do), or one the newer version requires that it leaves
/// empty or may leave out (<paramref name="Requirement"/>, the gate's own words on it).
/// </summary>
/// <param name="Path">The variable, as a template path.</param>
/// <param name="Change">What happened to a variable the mapping writes; null for a requirement.</param>
/// <param name="Fields">The fields of <paramref name="Change"/> that break the mapping: every field of a removed variable.</param>
/// <param name="Requirement">What the newer version requires of the mapping, said as the gate says it; null for a change.</param>
public sealed record MappingMoveFinding(string Path, TemplateVariableChange? Change, IReadOnlyList<TemplateFieldChange> Fields, string? Requirement);

/// <summary>A mapping set against a newer version of its kind: where it stands, and everything it has to change, in template order.</summary>
public sealed record MappingMoveCheck(MappingMoveState State, IReadOnlyList<MappingMoveFinding> Findings);

/// <summary>
/// What moving a mapping to a newer version of its kind takes (osdu/docs/reference/concepts/template-versions.md, Synced
/// mappings): the template it pins is compared with the newer one (<see cref="TemplateComparer"/>), and what it writes is set
/// against the breaking changes; then the mapping, pinned to the newer version, is held to the rule the delivery gate holds
/// required properties to (<see cref="MappingCoverage"/>), and whatever that finds and the pinned version did not is what the
/// move adds. Nothing is rendered and no cache is read, so the checks of the gate that need one are left to it.
/// </summary>
public static class MappingMoves
{
    /// <summary>Sets <paramref name="mapping"/>, which fills <paramref name="pinned"/>, against <paramref name="target"/>.</summary>
    public static MappingMoveCheck Check(MappingDefinition mapping, OsduTemplate pinned, OsduTemplate target)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(target);

        var comparison = TemplateComparer.Compare(pinned, target);
        if (string.Equals(pinned.Version, target.Version, StringComparison.Ordinal) || comparison.Changes.Count == 0)
        {
            return new MappingMoveCheck(MappingMoveState.Current, []);
        }

        // What the mapping writes: every entry's target, the properties of a list's items included, and what a literal or an
        // object written whole holds. A variable is written when any of these is it or lies inside it.
        var pinnedCoverage = MappingCoverage.Of(mapping, pinned);
        var written = mapping.Entries.SelectMany(entry => entry.Fillers).Select(filler => filler.Target.Text)
            .Concat(pinnedCoverage.Variables.Where(variable => variable.WrittenBy is not null).Select(variable => variable.Target))
            .ToHashSet(StringComparer.Ordinal);
        bool Writes(string path) => written.Any(target => TemplatePath.IsWithin(target, path));

        var breaking = comparison.Changes
            .Where(change => change.Impact == TemplateChangeImpact.Breaking && change.Change != TemplateVariableChangeKind.Added && Writes(change.Path))
            .ToList();
        var removed = breaking.Where(change => change.Change == TemplateVariableChangeKind.Removed).Select(change => change.Path).ToList();
        var findings = new List<MappingMoveFinding>();
        foreach (var change in breaking)
        {
            if (change.Change == TemplateVariableChangeKind.Removed)
            {
                // A removed object is the finding; what it held went with it.
                if (!removed.Any(outer => !string.Equals(outer, change.Path, StringComparison.Ordinal) && TemplatePath.IsWithin(change.Path, outer)))
                {
                    findings.Add(new MappingMoveFinding(change.Path, change, change.Fields, null));
                }

                continue;
            }

            // Whether the mapping fills a variable on every row is judged below, against the newer version, so a variable that
            // became required is a finding only when the mapping may leave it out.
            var fields = change.Fields.Where(f => f.Impact == TemplateChangeImpact.Breaking && f.Field != TemplateChangeAdvice.RequiredField).ToList();
            if (fields.Count > 0)
            {
                findings.Add(new MappingMoveFinding(change.Path, change, fields, null));
            }
        }

        // A fragment adds paths to a record another mapping renders whole, which is the one held to what is required.
        if (!mapping.Fragment)
        {
            var where = mapping.SourcePath ?? mapping.Reference;
            var known = pinnedCoverage.Issues.Select(issue => (issue.Target, issue.Severity)).ToHashSet();
            var moved = mapping with { Template = new TemplateReference(target.Kind, target.Version) };
            foreach (var issue in MappingCoverage.Of(moved, target).Issues)
            {
                if (issue.Target is { } path && !known.Contains((issue.Target, issue.Severity)))
                {
                    findings.Add(new MappingMoveFinding(path, null, [], Unnamed(issue.Message, where)));
                }
            }
        }

        return new MappingMoveCheck(findings.Count == 0 ? MappingMoveState.Ready : MappingMoveState.NeedsChanges, findings);
    }

    /// <summary>A gate message without the document it names first, which a report already names it by.</summary>
    private static string Unnamed(string message, string where)
        => message.StartsWith(where + ": ", StringComparison.Ordinal) ? message[(where.Length + 2)..] : message;
}
