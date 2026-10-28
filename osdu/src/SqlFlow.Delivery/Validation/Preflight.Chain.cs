using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Validation;

// The checks of a flow's enrich: chain as a whole (osdu/docs/reference/flow/enrich.md, What the preflight checks): each
// fragment is checked against its template like any mapping is (Check), and these check what no fragment can say alone,
// that the chain adds to the record without taking back what the flow's mapping or another step wrote, reads only what is
// written before it renders, and reads only the rows its step has.
public static partial class Preflight
{
    /// <summary>
    /// The problems of the flow's <c>enrich:</c> chain as a whole: every value the flow supplies is declared by a mapping of
    /// the chain; no step writes what the flow's mapping or another step writes, unless both add items to one list; no step
    /// writes under a key the flow preserves from the record OSDU holds, but for a step's files linked from the preserved list
    /// itself, which an update merges with what OSDU holds; every path a step reads with <c>$base</c> is written by
    /// the flow's mapping or a step that renders before it; and every column a fragment reads is a column of its step's rows.
    /// </summary>
    /// <param name="flow">The flow, whose <see cref="FlowDefinition.Enrich"/> lists the steps in the order they render.</param>
    /// <param name="mapping">The flow's own mapping, which renders the record the steps add to.</param>
    /// <param name="schema">The template the flow's mapping and every fragment fill.</param>
    /// <param name="steps">The steps whose fragments resolved, in the order they render; a step that did not is left out.</param>
    /// <param name="where">The flow, for messages.</param>
    public static IReadOnlyList<ValidationIssue> CheckChain(
        FlowDefinition flow, MappingDefinition mapping, SchemaSnapshot schema, IReadOnlyList<ResolvedStep> steps, string where)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(steps);
        var issues = new List<ValidationIssue>();

        // A flow supplies one render.parameters block to the whole chain: a value none of its mappings declares would be an
        // override, which no mapping allows (osdu/docs/reference/flow/mapping.md, parameters).
        var declared = new HashSet<string>(
            mapping.Parameters.Keys
                .Concat(steps.SelectMany(s => s.Mapping?.Parameters.Keys ?? []))
                .Concat(steps.SelectMany(s => s.Files?.Mapping?.Parameters.Keys ?? [])),
            StringComparer.Ordinal);
        foreach (var name in flow.Render.Parameters.Keys.Where(name => !declared.Contains(name)).Order(StringComparer.Ordinal))
        {
            issues.Add(ValidationIssue.Error(
                $"{where}: render.parameters supplies '{name}', which neither the flow's mapping {mapping.Reference} nor the fragment of any step of enrich: declares. Undeclared values would be overrides, and overrides are not allowed (osdu/docs/reference/flow/mapping.md, parameters)."));
        }

        var preserved = flow.Target.ProtocolOptions.PreserveDataKeys;
        var written = new List<(string Writer, Write Write)>();
        foreach (var write in Writes(mapping, schema))
        {
            written.Add(($"the flow's mapping {mapping.Reference}", write));
        }

        var stepWrites = steps.ToDictionary(s => s.Step.Name, s => StepWrites(s, schema), StringComparer.Ordinal);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var at = $"{where}: enrich.{step.Step.Name}";
            CheckRows(step, at, issues);
            CheckReads(step, i, steps, stepWrites, mapping, schema, at, issues);
            CheckLink(step, schema, at, issues);

            foreach (var write in stepWrites[step.Step.Name])
            {
                // A step's links in a preserved list are merged with what OSDU holds (osdu/docs/reference/flow/enrich-files.md,
                // Preserved keys); any other write there would be replaced by OSDU's value on every update.
                if (write.Path.Count > 1 && write.Path[0] == "data" && preserved.Contains(write.Path[1], StringComparer.Ordinal)
                    && !(write.Link && write.Path.Count == 2))
                {
                    var preserveDataKeys = KeyPaths.Of(flow).Shared("target.protocolOptions.preserveDataKeys");
                    issues.Add(ValidationIssue.Error(
                        write.Link
                            ? $"{at}: files.link names {write.Text}, inside data.{write.Path[1]}, which {preserveDataKeys} carries over whole from the record OSDU holds into every update: an update merges a step's links into what OSDU holds only in the preserved list itself. Link the datasets from a list the flow preserves (data.{write.Path[1]}, when it is one), or from one it does not."
                            : $"{at}: {Writer(step)} writes {write.Text}, and {preserveDataKeys} carries data.{write.Path[1]} over from the record OSDU holds into every update, in place of what is rendered: what the step adds there would reach a record only when it is created. Only a step's files may link datasets there (files.link), since an update merges those links with what OSDU holds; take {write.Path[1]} out of preserveDataKeys, or write somewhere else.",
                        write.Text));
                }

                if (written.FirstOrDefault(w => Overlaps(w.Write, write)) is { Writer: not null } earlier)
                {
                    issues.Add(ValidationIssue.Error(
                        $"{at}: {Writer(step)} writes {write.Text}, {Conflict(earlier.Write, write)} {earlier.Writer} writes; a step adds to the record without taking back what the flow's mapping or another step wrote, so two writers share a path only when both add items to one list (osdu/docs/reference/flow/enrich.md, How a fragment adds to the record).",
                        write.Text));
                }
            }

            foreach (var write in stepWrites[step.Step.Name])
            {
                written.Add(($"step '{step.Step.Name}' ({step.Mapping?.Reference ?? "its files"})", write));
            }
        }

        return issues;
    }

    /// <summary>
    /// One path a mapping writes into the record: the property names from the record's root, whether the mapping adds items to
    /// a list there (a <c>$forEach</c>, a list of values, or a property the template makes a list), how a message names it, and
    /// whether it is the list a step's files are linked from (<c>files.link</c>) rather than a fragment's write.
    /// </summary>
    private sealed record Write(IReadOnlyList<string> Path, bool List, string Text, bool Link = false);

    /// <summary>What a message names a step's writes by: its fragment, or its files for a step that registers files alone.</summary>
    private static string Writer(ResolvedStep step) => step.Mapping is { } fragment ? $"fragment {fragment.Reference}" : "the step's files";

    /// <summary>
    /// The paths a step writes into the record: its fragment's, and, for a step that registers files, the list its datasets are
    /// linked from, which it adds items to.
    /// </summary>
    private static List<Write> StepWrites(ResolvedStep step, SchemaSnapshot schema)
    {
        var writes = step.Mapping is { } fragment ? Writes(fragment, schema) : [];
        if (step.Files is { } files && !writes.Any(w => string.Equals(w.Text, files.Files.Link, StringComparison.Ordinal)))
        {
            writes.Add(new Write(files.Files.Link.Split('.'), true, files.Files.Link, Link: true));
        }

        return writes;
    }

    /// <summary>
    /// The path a step's datasets are linked from is a list of the template that takes their ids, or lies in an object the
    /// template leaves open: the links are added to it as items, <c>{id}:</c>.
    /// </summary>
    private static void CheckLink(ResolvedStep step, SchemaSnapshot schema, string at, List<ValidationIssue> issues)
    {
        if (step.Files is not { } files)
        {
            return;
        }

        var link = files.Files.Link;
        if (ParentProperty(schema, link) is not { } property)
        {
            issues.Add(ValidationIssue.Error(
                $"{at}: files.link names {link}, and template {schema.Kind} version {schema.Version} declares no {link}; name the list the record keeps its datasets in, such as {EnrichFiles.DefaultLink}.",
                link));
            return;
        }

        if (property.Type is not (SchemaType.Array or SchemaType.Any) || (property.Type == SchemaType.Array && property.ItemScalarType is SchemaType.Object))
        {
            issues.Add(ValidationIssue.Error(
                $"{at}: files.link names {link}, which template {schema.Kind} declares as {ShapeOf(property)}; the datasets are listed by their ids, so the link is a list of values, such as {EnrichFiles.DefaultLink}.",
                link));
        }
    }

    /// <summary>
    /// The paths <paramref name="mapping"/> writes into the record, each once: every entry of the record's own, and every
    /// repeated array as a whole, never the properties inside its items, which are the array's.
    /// </summary>
    private static List<Write> Writes(MappingDefinition mapping, SchemaSnapshot schema)
        => mapping.Entries
            .Where(entry => !entry.Target.IsRepeated)
            .Select(entry => new Write(
                entry.Target.Segments.Select(s => s.Name).ToList(),
                entry.IsRepeater || entry.IsList || schema.Resolve(entry.Target.SchemaPath)?.Type == SchemaType.Array,
                string.Join('.', entry.Target.Segments.Select(s => s.Name))))
            .DistinctBy(write => write.Text, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Whether two writes reach the same value: the same path, unless both add items to a list there, or one path inside the
    /// other, whose writer writes the outer value whole.
    /// </summary>
    private static bool Overlaps(Write earlier, Write later)
    {
        if (earlier.Path.SequenceEqual(later.Path, StringComparer.Ordinal))
        {
            return !(earlier.List && later.List);
        }

        return Within(earlier.Path, later.Path) || Within(later.Path, earlier.Path);
    }

    /// <summary>True when <paramref name="inner"/> lies below <paramref name="outer"/>.</summary>
    private static bool Within(IReadOnlyList<string> outer, IReadOnlyList<string> inner)
        => inner.Count > outer.Count && outer.SequenceEqual(inner.Take(outer.Count), StringComparer.Ordinal);

    /// <summary>How two overlapping writes meet, as the sentence naming the earlier writer says it.</summary>
    private static string Conflict(Write earlier, Write later)
    {
        if (earlier.Path.SequenceEqual(later.Path, StringComparer.Ordinal))
        {
            return earlier.List || later.List
                ? "which is a list one of them adds items to and the other writes as a value, and"
                : "which";
        }

        return Within(earlier.Path, later.Path)
            ? $"inside {earlier.Text}, which"
            : $"the whole of what holds {earlier.Text}, which";
    }

    /// <summary>
    /// The columns a step's fragment reads are its step's rows: under <c>rows: one</c> the one row, read as the fragment's own,
    /// and the rows under a <c>$forEach</c> over the step's name; under <c>rows: many</c> the rows under that <c>$forEach</c>
    /// alone; and none for a step that reads no rows. A <c>$forEach</c> repeats the step's own rows, the only child dataset a
    /// fragment has.
    /// </summary>
    private static void CheckRows(ResolvedStep step, string at, List<ValidationIssue> issues)
    {
        if (step.Mapping is null)
        {
            return;
        }

        foreach (var repeater in step.Mapping.Entries.Where(e => e.IsRepeater))
        {
            var repeated = repeater.Source!.Child!;
            if (!step.Step.ReadsRows || !string.Equals(repeated, step.Step.Name, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(ValidationIssue.Error(step.Step.ReadsRows
                    ? $"{at}: fragment {step.Mapping.Reference} repeats the rows of {repeated} at {repeater.Where}, and a fragment reads its step's rows alone: write $forEach: {step.Step.Name}."
                    : $"{at}: fragment {step.Mapping.Reference} repeats the rows of {repeated} at {repeater.Where}, and the step declares no dataset, so it has no rows to repeat; declare enrich.{step.Step.Name}.dataset.",
                    repeater.Target.Text));
            }
        }

        foreach (var entry in step.Mapping.Entries.Where(e => !e.Target.IsRepeated && !e.IsRepeater))
        {
            if (entry.Columns.FirstOrDefault(c => c.Child is null) is not { } column)
            {
                continue;
            }

            if (!step.Step.ReadsRows)
            {
                issues.Add(ValidationIssue.Error(
                    $"{at}: fragment {step.Mapping.Reference} reads {column} at {entry.Where}, and the step declares no dataset, so it has no row to read; declare enrich.{step.Step.Name}.dataset, or read what the record holds with $base.",
                    entry.Target.Text));
            }
            else if (step.Step.Rows == EnrichRows.Many)
            {
                issues.Add(ValidationIssue.Error(
                    $"{at}: fragment {step.Mapping.Reference} reads {column} at {entry.Where}, outside a $forEach, and the step takes many rows per record (rows: many), so no one row is the fragment's own; read the rows under $forEach: {step.Step.Name}, or declare rows: one.",
                    entry.Target.Text));
            }
        }
    }

    /// <summary>
    /// Every path a step reads with <c>$base</c> is written, at that path or around it, by the flow's mapping or a step that
    /// renders before this one, since the record the step reads is the one those left; a path only a later step writes, or the
    /// step itself, is not there yet when it renders. Each path is also one the template declares, or lies inside an object the
    /// template leaves open.
    /// </summary>
    private static void CheckReads(
        ResolvedStep step,
        int index,
        IReadOnlyList<ResolvedStep> steps,
        IReadOnlyDictionary<string, List<Write>> stepWrites,
        MappingDefinition mapping,
        SchemaSnapshot schema,
        string at,
        List<ValidationIssue> issues)
    {
        var before = Writes(mapping, schema).Concat(steps.Take(index).SelectMany(s => stepWrites[s.Step.Name])).ToList();
        var reads = (step.Mapping?.Entries ?? []).Concat(step.Files?.Mapping?.Entries ?? []).SelectMany(e => e.ValueNodes);
        foreach (var node in reads.Where(n => n.Source is { Kind: MappingSourceKind.Base }))
        {
            var path = node.Source!.BasePath!;
            if (ParentProperty(schema, path) is null)
            {
                issues.Add(ValidationIssue.Error(
                    $"{at}: {node.Where} reads {path} with $base, and template {schema.Kind} version {schema.Version} declares no {path}.",
                    node.Target.Text));
                continue;
            }

            var read = path.Split('.');
            bool Reaches(Write w) => w.Path.SequenceEqual(read, StringComparer.Ordinal) || Within(w.Path, read) || Within(read, w.Path);
            if (before.Any(Reaches))
            {
                continue;
            }

            var later = steps.Skip(index).FirstOrDefault(s => stepWrites[s.Step.Name].Any(Reaches));
            issues.Add(ValidationIssue.Error(later is null
                ? $"{at}: {node.Where} reads {path} with $base, and neither the flow's mapping {mapping.Reference} nor a step that renders before this one writes it, so the record never holds it when the step renders."
                : ReferenceEquals(later, step)
                    ? $"{at}: {node.Where} reads {path} with $base, which the step itself writes; a step reads the record as the flow's mapping and the steps before it left it."
                    : $"{at}: {node.Where} reads {path} with $base, which step '{later.Step.Name}' writes after this one renders; name it under enrich.{step.Step.Name}.after so this step renders after it.",
                node.Target.Text));
        }
    }
}
