using SqlFlow.Delivery.Documents;
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
/// What a difference between the version a mapping pins and a newer one means for that mapping, read against what its
/// document writes; declared in the order a report lists them, what it has to act on first.
/// </summary>
public enum MappingFindingKind
{
    /// <summary>Something it writes changed in a way that breaks it, or the newer version requires something it leaves empty.</summary>
    MustChange,

    /// <summary>The newer version marks something it writes deprecated: still accepted, and its description says what replaces it.</summary>
    Deprecated,

    /// <summary>Something it writes changed without breaking it: how it reads, or what it asks of a value (no longer required, points to more kinds).</summary>
    Changed,

    /// <summary>A new optional variable inside an object it writes, the record's data included: something it may now fill.</summary>
    MayFill,
}

/// <summary>How a mapping writes a variable: where its document writes it, and what the value is made from.</summary>
/// <param name="Line">The line of the document the node starts on, from 1; null where the document's text was not at hand.</param>
/// <param name="Reads">
/// The node on one line, as the mapping's overview shows it (<c>dataset.log_run | trim</c>, <c>cache.UnitOfMeasure.id by Code =
/// dataset.unit</c>, a literal as JSON); for a variable written inside a literal or an object written whole, what writes it.
/// </param>
public sealed record MappingWrite(int? Line, string Reads);

/// <summary>
/// One thing moving to a newer version of its kind means for a mapping, by <paramref name="Kind"/>: a variable it writes that
/// changed in a way that breaks it (<paramref name="Change"/>, with the fields that do) or that the newer version requires
/// while it may leave it out (<paramref name="Requirement"/>, the gate's own words); one the newer version deprecates; one
/// that changed without breaking it; or a new one it may fill.
/// </summary>
/// <param name="Path">The variable, as a template path.</param>
/// <param name="Kind">What it means for the mapping.</param>
/// <param name="Change">What happened to the variable between the two versions; null for a requirement, and for a variable deprecated in both.</param>
/// <param name="Fields">
/// The fields of <paramref name="Change"/> the finding is about: those that break the mapping (every field of a removed
/// variable), or every field that changed for one that did not break it.
/// </param>
/// <param name="Requirement">What the newer version requires of the mapping, said as the gate says it; null otherwise.</param>
public sealed record MappingMoveFinding(
    string Path, MappingFindingKind Kind, TemplateVariableChange? Change, IReadOnlyList<TemplateFieldChange> Fields, string? Requirement)
{
    /// <summary>How the mapping writes the variable; null for an object it only writes inside, and for a new variable it may fill.</summary>
    public MappingWrite? Write { get; init; }

    /// <summary>What the newer version's description says to use instead, for a variable it deprecates (<see cref="TemplateDeprecation.Guidance"/>).</summary>
    public string? Deprecation { get; init; }

    /// <summary>True when the version the mapping pins already deprecated the variable, so the move does not.</summary>
    public bool AlreadyDeprecated { get; init; }
}

/// <summary>
/// A mapping set against a newer version of its kind: where it stands, everything the move means for it, grouped by
/// <see cref="MappingFindingKind"/> in the order its document writes what each is about, and what it writes, which a change
/// of the kind is weighed against.
/// </summary>
/// <param name="State">Where it stands.</param>
/// <param name="Findings">What the move means for it.</param>
public sealed record MappingMoveCheck(MappingMoveState State, IReadOnlyList<MappingMoveFinding> Findings)
{
    /// <summary>
    /// Every variable the mapping writes, as template paths of the version it pins, in the order its document writes them:
    /// what its entries target and what a literal or an object written whole holds. Empty for a mapping that already pins the
    /// newer version or could not be checked.
    /// </summary>
    public IReadOnlyList<string> Writes { get; init; } = [];

    /// <summary>How many findings are of <paramref name="kind"/>.</summary>
    public int Count(MappingFindingKind kind) => Findings.Count(f => f.Kind == kind);

    /// <summary>
    /// Whether a change between two versions of the kind touches what the mapping uses: a variable it writes, an object it
    /// writes inside, or the object a newly required variable sits in. A new optional variable touches no mapping: it is
    /// something a mapping may fill, never something it relies on.
    /// </summary>
    public bool Uses(TemplateVariableChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Change != TemplateVariableChangeKind.Added)
        {
            return WritesInside(change.Path);
        }

        return change.Impact == TemplateChangeImpact.Breaking
            && TemplatePath.TryParse(change.Path, out var path, out _)
            && path!.Parent is { } holder
            && WritesInside(holder.Text);
    }

    /// <summary>Whether the mapping writes the variable at <paramref name="path"/> or anything inside it.</summary>
    public bool WritesInside(string path) => Writes.Any(target => TemplatePath.IsWithin(target, path));
}

/// <summary>
/// What moving a mapping to a newer version of its kind means for it (osdu/docs/reference/concepts/template-versions.md, Synced
/// mappings). The template it pins is compared with the newer one (<see cref="TemplateComparer"/>), and every change is read
/// against what its document writes: a change to what it writes that breaks it, or a variable the newer version requires
/// while the mapping, pinned to it, would leave it out by the delivery gate's rule (<see cref="MappingCoverage"/>), is what it
/// has to change; a variable it writes that the newer version deprecates, or that changed without breaking it, is said with
/// how the document writes it; a new optional variable inside an object it writes is something it may fill. A change to
/// nothing it writes means nothing for it. Nothing is rendered and no cache is read, so the checks of the gate that need one
/// are left to it.
/// </summary>
public static class MappingMoves
{
    /// <summary>
    /// Sets <paramref name="mapping"/>, which fills <paramref name="pinned"/>, against <paramref name="target"/>.
    /// <paramref name="yaml"/> is the mapping's document, which gives each finding the line its node starts on; without it
    /// the findings carry no lines.
    /// </summary>
    public static MappingMoveCheck Check(MappingDefinition mapping, OsduTemplate pinned, OsduTemplate target, string? yaml = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(target);

        var comparison = TemplateComparer.Compare(pinned, target);
        if (string.Equals(pinned.Version, target.Version, StringComparison.Ordinal) || comparison.Changes.Count == 0)
        {
            return new MappingMoveCheck(MappingMoveState.Current, []);
        }

        var pinnedCoverage = MappingCoverage.Of(mapping, pinned);
        var writes = Written(mapping, pinnedCoverage, yaml);
        bool Writes(string path) => writes.Keys.Any(written => TemplatePath.IsWithin(written, path));
        MappingWrite? WriteOf(string path) => writes.GetValueOrDefault(path);

        var mustChange = MustChange(mapping, pinnedCoverage, target, comparison, Writes, WriteOf);
        var settled = mustChange.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        // What the newer version deprecates of what the mapping writes, in its order: deprecated by this move, or already in
        // the version the mapping pins, which it still writes all the same.
        var changes = comparison.Changes.Where(c => c.Change != TemplateVariableChangeKind.Added).ToDictionary(c => c.Path, StringComparer.Ordinal);
        var deprecated = new List<MappingMoveFinding>();
        foreach (var variable in target.Variables)
        {
            var path = variable.Path.Text;
            if (!TemplateDeprecation.IsDeprecated(variable.Description) || !Writes(path))
            {
                continue;
            }

            var guidance = TemplateDeprecation.Guidance(variable.Description);
            var already = TemplateDeprecation.IsDeprecated(pinned.Find(variable.Path)?.Description);
            var change = changes.GetValueOrDefault(path);
            if (settled.Contains(path))
            {
                // What the mapping has to change anyway says the newer version deprecates it, so it is not said twice.
                var at = mustChange.FindIndex(f => f.Path == path);
                mustChange[at] = mustChange[at] with { Deprecation = guidance, AlreadyDeprecated = already };
                continue;
            }

            deprecated.Add(new MappingMoveFinding(path, MappingFindingKind.Deprecated, change, change?.Fields ?? [], null)
            {
                Write = WriteOf(path),
                Deprecation = guidance,
                AlreadyDeprecated = already,
            });
            settled.Add(path);
        }

        // Everything else that changed about what it writes, which does not break it: how it reads, a rule it no longer has
        // to keep, more kinds it may point to, a requirement it already meets on every row.
        var changed = comparison.Changes
            .Where(c => c.Change == TemplateVariableChangeKind.Changed && !settled.Contains(c.Path) && Writes(c.Path))
            .Select(c => new MappingMoveFinding(c.Path, MappingFindingKind.Changed, c, c.Fields, null) { Write = WriteOf(c.Path) })
            .ToList();

        // A new optional variable inside an object it writes: the record's data, or an object or a list's items it fills. One
        // inside a new object is part of that object, which is the one listed.
        var mayFill = comparison.Changes
            .Where(c => c is { Change: TemplateVariableChangeKind.Added, Impact: TemplateChangeImpact.Additive, Role: TemplateVariableRole.Mapping }
                && TemplatePath.TryParse(c.Path, out var path, out _) && path!.Parent is { } holder && Writes(holder.Text)
                && target.Find(path) is { Nested: false })
            .Select(c => new MappingMoveFinding(c.Path, MappingFindingKind.MayFill, c, c.Fields, null))
            .ToList();

        // Each kind in the order the document writes what it is about, so a reader goes down the file once; a requirement the
        // mapping does not write, and a new variable it may fill, keep the comparison's order after.
        var order = writes.Keys.ToList();
        int Place(MappingMoveFinding finding) => finding.Kind == MappingFindingKind.MayFill
            ? int.MaxValue
            : order.FindIndex(written => TemplatePath.IsWithin(written, finding.Path)) is var at and >= 0 ? at : int.MaxValue;
        var findings = mustChange.Concat(deprecated).Concat(changed).Concat(mayFill).OrderBy(f => f.Kind).ThenBy(Place).ToList();
        var state = mustChange.Count == 0 ? MappingMoveState.Ready : MappingMoveState.NeedsChanges;
        return new MappingMoveCheck(state, findings) { Writes = writes.Keys.ToList() };
    }

    /// <summary>
    /// What the mapping has to change: what it writes that changed in a way that breaks it, in the comparison's order, then
    /// what the newer version requires that it would leave out, as the gate says it.
    /// </summary>
    private static List<MappingMoveFinding> MustChange(
        MappingDefinition mapping,
        CoverageReport pinnedCoverage,
        OsduTemplate target,
        TemplateComparison comparison,
        Func<string, bool> writes,
        Func<string, MappingWrite?> writeOf)
    {
        var breaking = comparison.Changes
            .Where(change => change.Impact == TemplateChangeImpact.Breaking && change.Change != TemplateVariableChangeKind.Added && writes(change.Path))
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
                    findings.Add(new MappingMoveFinding(change.Path, MappingFindingKind.MustChange, change, change.Fields, null) { Write = writeOf(change.Path) });
                }

                continue;
            }

            // Whether the mapping fills a variable on every row is judged below, against the newer version, so a variable that
            // became required is a finding only when the mapping may leave it out.
            var fields = change.Fields.Where(f => f.Impact == TemplateChangeImpact.Breaking && f.Field != TemplateChangeAdvice.RequiredField).ToList();
            if (fields.Count > 0)
            {
                findings.Add(new MappingMoveFinding(change.Path, MappingFindingKind.MustChange, change, fields, null) { Write = writeOf(change.Path) });
            }
        }

        // A fragment adds paths to a record another mapping renders whole, which is the one held to what is required.
        if (mapping.Fragment)
        {
            return findings;
        }

        var where = mapping.SourcePath ?? mapping.Reference;
        var known = pinnedCoverage.Issues.Select(issue => (issue.Target, issue.Severity)).ToHashSet();
        var moved = mapping with { Template = new TemplateReference(target.Kind, target.Version) };
        foreach (var issue in MappingCoverage.Of(moved, target).Issues)
        {
            if (issue.Target is { } path && !known.Contains((issue.Target, issue.Severity)))
            {
                findings.Add(new MappingMoveFinding(path, MappingFindingKind.MustChange, null, [], Unnamed(issue.Message, where)) { Write = writeOf(path) });
            }
        }

        return findings;
    }

    /// <summary>
    /// Every variable the mapping writes, in the order its document writes them, with how it writes it: each entry's target
    /// (the properties of a list's items included), described as the mapping's overview describes its node
    /// (<see cref="MappingOutline"/>), then what a literal or an object written whole holds, described by what writes it.
    /// </summary>
    private static Dictionary<string, MappingWrite> Written(MappingDefinition mapping, CoverageReport coverage, string? yaml)
    {
        var outlined = new Dictionary<string, MappingOutlineNode>(StringComparer.Ordinal);
        foreach (var node in MappingOutline.Of(mapping, schema: null, yaml).Record)
        {
            Index(outlined, node);
        }

        var written = new Dictionary<string, MappingWrite>(StringComparer.Ordinal);
        foreach (var target in mapping.Entries.SelectMany(entry => entry.Fillers).Select(filler => filler.Target.Text))
        {
            if (!written.ContainsKey(target))
            {
                var node = outlined.GetValueOrDefault(target);
                written[target] = new MappingWrite(node?.Line, node is null ? "an entry of the mapping" : Reads(node));
            }
        }

        foreach (var variable in coverage.Variables.Where(v => v.WrittenBy is not null && !written.ContainsKey(v.Target)))
        {
            var holder = variable.WrittenBy!;
            var reads = variable.Values is { Count: > 0 } values
                ? $"{string.Join(", ", values)}, in what {holder} writes"
                : $"what {holder} writes";
            written[variable.Target] = new MappingWrite(outlined.GetValueOrDefault(holder)?.Line, reads);
        }

        return written;
    }

    /// <summary>Indexes an outline's nodes by the template path of the variable each fills; a node's alternatives and items, at places of their own, fill none.</summary>
    private static void Index(Dictionary<string, MappingOutlineNode> outlined, MappingOutlineNode node)
    {
        outlined.TryAdd(TemplatePath.TextOf(node.Path), node);
        foreach (var child in node.Children)
        {
            Index(outlined, child);
        }
    }

    /// <summary>What a node makes its value from, on one line: its flow, its alternatives in order, its rows, or its items.</summary>
    private static string Reads(MappingOutlineNode node) => node.Shape switch
    {
        MappingOutlineShape.Choice => string.Join(" or else ", node.Children.Select(child => child.Flow?.Summary).OfType<string>()),
        MappingOutlineShape.Repeat => $"one item per row of {node.Dataset}",
        MappingOutlineShape.List => $"[{string.Join(", ", node.Children.Select(child => child.Flow?.Summary ?? "{...}"))}]",
        MappingOutlineShape.Group => "what it holds",
        _ => node.Flow?.Summary ?? "an entry of the mapping",
    };

    /// <summary>A gate message without the document it names first, which a report already names it by.</summary>
    private static string Unnamed(string message, string where)
        => message.StartsWith(where + ": ", StringComparison.Ordinal) ? message[(where.Length + 2)..] : message;
}
