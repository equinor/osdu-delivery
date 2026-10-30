using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Validation;

// The checks of a mapping's enrichments as a whole (osdu/docs/reference/flow/enrich.md, What the preflight checks): each
// enrichment mapping is checked against its template like any mapping is (Check), and these check what no enrichment mapping
// can say alone, that the chain adds to the record without taking back what the mapping or another enrichment wrote, reads
// only what is written before it renders, reads only the rows its enrichment has, and links what the flow keeps current.
public static partial class Preflight
{
    /// <summary>
    /// The problems of the mapping's enrichments as a whole: every value the flow supplies is declared by a mapping of the
    /// chain; no enrichment writes what the mapping or another enrichment writes, unless both add items to one list; no
    /// enrichment writes under a key the flow preserves from the record OSDU holds; a <c>$link</c> sits in a preserved list
    /// only as the list itself, which an update merges with what OSDU holds, and writes one value only for an enrichment taking
    /// one row; every path an enrichment reads with <c>$base</c> is written by the mapping or an enrichment that renders before
    /// it; and every column an enrichment mapping reads is a column of its enrichment's rows.
    /// </summary>
    /// <param name="flow">The flow, whose <see cref="FlowDefinition.Enrich"/> lists the enrichments in the order they render.</param>
    /// <param name="mapping">The flow's own mapping, which renders the record the enrichments add to.</param>
    /// <param name="schema">The template the flow's mapping and every enrichment mapping adding paths fill.</param>
    /// <param name="steps">The enrichments that resolved, in the order they render; one that did not is left out.</param>
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
                .Concat(steps.SelectMany(s => s.Records?.Mapping?.Parameters.Keys ?? [])),
            StringComparer.Ordinal);
        foreach (var name in flow.Render.Parameters.Keys.Where(name => !declared.Contains(name)).Order(StringComparer.Ordinal))
        {
            issues.Add(ValidationIssue.Error(
                $"{where}: render.parameters supplies '{name}', which neither the flow's mapping {mapping.Reference} nor the mapping of any of its enrichments declares. Undeclared values would be overrides, and overrides are not allowed (osdu/docs/reference/flow/mapping.md, parameters)."));
        }

        var preserved = flow.Target.ProtocolOptions.PreserveDataKeys;
        CheckLinks(flow, mapping, schema, steps, where, issues);
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

            foreach (var write in stepWrites[step.Step.Name])
            {
                // A preserved key is carried over from the record OSDU holds into every update, in place of what is rendered.
                if (write.Path.Count > 1 && write.Path[0] == "data" && preserved.Contains(write.Path[1], StringComparer.Ordinal))
                {
                    var preserveDataKeys = KeyPaths.Of(flow).Shared("target.protocolOptions.preserveDataKeys");
                    issues.Add(ValidationIssue.Error(
                        $"{at}: {Writer(step)} writes {write.Text}, and {preserveDataKeys} carries data.{write.Path[1]} over from the record OSDU holds into every update, in place of what is rendered: what the enrichment adds there would reach a record only when it is created. Only the datasets the mapping links there with $link merge with what OSDU holds; take {write.Path[1]} out of preserveDataKeys, or write somewhere else.",
                        write.Text));
                }

                if (written.FirstOrDefault(w => Overlaps(w.Write, write)) is { Writer: not null } earlier)
                {
                    issues.Add(ValidationIssue.Error(
                        $"{at}: {Writer(step)} writes {write.Text}, {Conflict(earlier.Write, write)} {earlier.Writer} writes; an enrichment adds to the record without taking back what the mapping or another enrichment wrote, so two writers share a path only when both add items to one list (osdu/docs/reference/flow/mapping-enrichment.md, How an enrichment adds to the record).",
                        write.Text));
                }
            }

            foreach (var write in stepWrites[step.Step.Name])
            {
                written.Add(($"enrichment '{step.Step.Name}' ({step.Mapping?.Reference ?? "its datasets"})", write));
            }
        }

        return issues;
    }

    /// <summary>
    /// One path a mapping writes into the record: the property names from the record's root, whether the mapping adds items to
    /// a list there (a <c>$forEach</c>, a list of values, or a property the template makes a list), and how a message names it.
    /// </summary>
    private sealed record Write(IReadOnlyList<string> Path, bool List, string Text);

    /// <summary>What a message names an enrichment's writes by: its enrichment mapping.</summary>
    private static string Writer(ResolvedStep step) => step.Mapping is { } fragment ? $"enrichment mapping {fragment.Reference}" : "the enrichment";

    /// <summary>The paths an enrichment writes into the record: its enrichment mapping's, when it adds paths; none when it registers datasets.</summary>
    private static List<Write> StepWrites(ResolvedStep step, SchemaSnapshot schema)
        => step.Mapping is { } fragment ? Writes(fragment, schema) : [];

    /// <summary>
    /// Where the mapping links the datasets its enrichments register (<c>$link</c>): a list the flow preserves only as the list
    /// itself (<c>data.Datasets</c> with <c>Datasets</c> in <c>preserveDataKeys</c>), which an update merges with what OSDU
    /// holds, never inside one, which an update carries over whole; and a single value only for an enrichment taking one row,
    /// whose one dataset is the value.
    /// </summary>
    private static void CheckLinks(
        FlowDefinition flow, MappingDefinition mapping, SchemaSnapshot schema, IReadOnlyList<ResolvedStep> steps, string where, List<ValidationIssue> issues)
    {
        var preserved = flow.Target.ProtocolOptions.PreserveDataKeys;
        var preserveDataKeys = KeyPaths.Of(flow).Shared("target.protocolOptions.preserveDataKeys");
        foreach (var node in mapping.Entries.SelectMany(e => e.ValueNodes).Where(n => n.Source is { Kind: MappingSourceKind.Link }))
        {
            var name = node.Source!.LinkName!;
            var path = node.Target.Segments.Select(s => s.Name).ToList();
            if (path.Count > 2 && path[0] == "data" && preserved.Contains(path[1], StringComparer.Ordinal))
            {
                issues.Add(ValidationIssue.Error(
                    $"{where}: {node.Where} links the datasets of enrich.{name} inside data.{path[1]}, which {preserveDataKeys} carries over whole from the record OSDU holds into every update: an update merges the links the mapping writes into what OSDU holds only in the preserved list itself. Link them from a list the flow preserves (data.{path[1]}, when it is one), or from one it does not.",
                    node.Target.Text));
            }

            if (steps.FirstOrDefault(s => s.Step.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Step.Rows: EnrichRows.Many } many
                && schema.Resolve(node.Target.SchemaPath) is { Type: not (SchemaType.Array or SchemaType.Any) } single)
            {
                issues.Add(ValidationIssue.Error(
                    $"{where}: {node.Where} links the datasets of enrich.{many.Step.Name}, which registers one for each of its rows (rows: many), and template {schema.Kind} takes {ShapeOf(single)} at {node.Target.Text}; link them from a list of values, such as Datasets: [ {{ $link: {many.Step.Name} }} ].",
                    node.Target.Text));
            }
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
    /// The columns an enrichment mapping adding paths reads are its enrichment's rows: under <c>rows: one</c> the one row, read
    /// as its own, and the rows under a <c>$forEach</c> over the enrichment's name; under <c>rows: many</c> the rows under that
    /// <c>$forEach</c> alone; and none for an enrichment that reads no rows. A <c>$forEach</c> repeats the enrichment's own
    /// rows, the only child dataset an enrichment mapping has.
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
                    ? $"{at}: enrichment mapping {step.Mapping.Reference} repeats the rows of {repeated} at {repeater.Where}, and an enrichment mapping reads its enrichment's rows alone: write $forEach: {step.Step.Name}."
                    : $"{at}: enrichment mapping {step.Mapping.Reference} repeats the rows of {repeated} at {repeater.Where}, and the enrichment reads no rows, so it has none to repeat; declare rows: in the mapping's enrich.{step.Step.Name}, and bind them under source.datasets.{step.Step.Name}.",
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
                    $"{at}: enrichment mapping {step.Mapping.Reference} reads {column} at {entry.Where}, and the enrichment reads no rows, so it has no row to read; declare rows: in the mapping's enrich.{step.Step.Name}, or read what the record holds with $base.",
                    entry.Target.Text));
            }
            else if (step.Step.Rows == EnrichRows.Many)
            {
                issues.Add(ValidationIssue.Error(
                    $"{at}: enrichment mapping {step.Mapping.Reference} reads {column} at {entry.Where}, outside a $forEach, and the enrichment takes many rows per record (rows: many), so no one row is its mapping's own; read the rows under $forEach: {step.Step.Name}, or declare rows: one.",
                    entry.Target.Text));
            }
        }
    }

    /// <summary>
    /// Every path an enrichment reads with <c>$base</c> is written, at that path or around it, by the mapping or an enrichment
    /// that renders before this one, since the record it reads is the one those left; a path only a later enrichment writes, or
    /// the enrichment itself, is not there yet when it renders. Each path is also one the template declares, or lies inside an
    /// object the template leaves open.
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
        var reads = (step.Mapping?.Entries ?? []).Concat(step.Records?.Mapping?.Entries ?? []).SelectMany(e => e.ValueNodes);
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
                ? $"{at}: {node.Where} reads {path} with $base, and neither the flow's mapping {mapping.Reference} nor an enrichment that renders before this one writes it, so the record never holds it when the enrichment renders."
                : ReferenceEquals(later, step)
                    ? $"{at}: {node.Where} reads {path} with $base, which the enrichment itself writes; an enrichment reads the record as the mapping and the enrichments before it left it."
                    : $"{at}: {node.Where} reads {path} with $base, which enrichment '{later.Step.Name}' writes after this one renders; name it under enrich.{step.Step.Name}.after so this one renders after it.",
                node.Target.Text));
        }
    }
}
