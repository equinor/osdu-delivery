using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Documents;

namespace SqlFlow.Delivery.Templates;

/// <summary>
/// A type a cache holds: the name mappings read it by, its entity type, the fields it captures, and for a lookup table the
/// name its key is kept under (null for a type of OSDU records).
/// </summary>
public sealed record CachedTypeInfo(string Name, string EntityType, IReadOnlyList<string> Fields, string? Key = null);

/// <summary>
/// The mapping builder's logic (osdu/docs/reference/concepts/gui-build.md, Mapping builder): a draft prefilled from the
/// template and the cache, the checks that say what is still missing, the YAML the draft is written as, and the draft of
/// an existing mapping. The YAML is the one written form; what it loads to is decided by the document loader alone.
/// </summary>
public static partial class MappingBuilder
{
    /// <summary>The access and legal variables every record carries, which a mapping gives as static lists.</summary>
    public static readonly IReadOnlyList<string> EnvelopeTargets =
    [
        "osdu.acl.owners",
        "osdu.acl.viewers",
        "osdu.legal.legaltags",
        "osdu.legal.otherRelevantDataCountries",
    ];

    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "yes", "no", "on", "off", "y", "n", "null", "~",
    };

    /// <summary>
    /// A new draft for a template: the envelope entries to fill, and a cache entry for every variable outside a repeater
    /// that points to an entity type the cache holds, finding the record by the type's first cached field.
    /// </summary>
    public static MappingDraft Draft(OsduTemplate template, IReadOnlyList<CachedTypeInfo> cache, string name, string version, string system)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(cache);
        var entries = EnvelopeTargets.Select(target => new MappingDraftEntry { Target = target, Input = MappingDraftInput.Static, Static = "[]" }).ToList();
        foreach (var variable in template.Fillable)
        {
            if (variable.Path.IsRepeated || variable.Shape is not (TemplateVariableShape.Value or TemplateVariableShape.ValueList)
                || variable.Relationships.Count == 0 || EnvelopeTargets.Contains(variable.Path.Text, StringComparer.Ordinal))
            {
                continue;
            }

            if (CacheTypesFor(variable, cache) is not { Count: > 0 } answering)
            {
                continue;
            }

            var cached = answering[0];

            entries.Add(new MappingDraftEntry
            {
                Target = variable.Path.Text,
                Input = MappingDraftInput.Cache,
                CacheType = cached.Name,
                CacheField = "id",
                FindBy = [new MappingDraftFind(cached.Fields.Count > 0 ? cached.Fields[0] : "id", string.Empty, null)],
                Prefilled = true,
            });
        }

        return new MappingDraft
        {
            Name = name?.Trim() ?? string.Empty,
            Version = version?.Trim() ?? string.Empty,
            TemplateKind = template.Kind,
            TemplateVersion = template.Version,
            System = system?.Trim() ?? string.Empty,
            Parameters = [new MappingDraftParameter(Snapshots.RenderContext.DataPartitionParameter, true, null, "The OSDU data partition record ids and references are minted in.")],
            Entries = entries,
        };
    }

    /// <summary>The cached types whose entity type a variable's relationships allow.</summary>
    public static IReadOnlyList<CachedTypeInfo> CacheTypesFor(TemplateVariable variable, IReadOnlyList<CachedTypeInfo> cache)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(cache);
        return cache.Where(c => variable.Relationships.Any(r => string.Equals(r, c.EntityType, StringComparison.Ordinal)
                || (!r.Contains("--", StringComparison.Ordinal) && c.EntityType.StartsWith(r + "--", StringComparison.Ordinal))))
            .ToList();
    }

    /// <summary>One property of the record tree the draft is written as: an object of properties, or an entry, which for a repeat holds the item's properties too.</summary>
    private sealed class TreeSlot
    {
        public MappingDraftEntry? Entry { get; init; }

        public List<(string Name, TreeSlot Slot)>? Properties { get; init; }
    }

    /// <summary>An entry the tree has no place for, with why: null when it only repeats a target another entry already fills.</summary>
    private sealed record LeftOutEntry(string Target, string? Reason);

    /// <summary>
    /// The draft's entries laid out as the record tree. <see cref="Scopes"/> gives, by entry index, the child dataset whose
    /// rows an entry inside a repeated array reads, and <see cref="EmptyRepeats"/> the repeats in the tree whose items hold
    /// no entry yet.
    /// </summary>
    private sealed record DraftLayout(
        List<(string Name, TreeSlot Slot)> Root, IReadOnlyList<LeftOutEntry> LeftOut, IReadOnlyDictionary<int, string?> Scopes, IReadOnlySet<int> EmptyRepeats);

    /// <summary>
    /// Places every entry of the draft at the property its target names. The entries outside repeated arrays go first, in
    /// draft order, so a repeat is in place before the entries of its items; each of those then goes under the repeat of its
    /// array. An entry that names a property inside one another entry fills whole, or that fills whole a property other
    /// entries fill inside, has no place, and neither has an entry of an array's items that nothing repeats.
    /// </summary>
    private static DraftLayout Layout(MappingDraft draft)
    {
        var root = new List<(string Name, TreeSlot Slot)>();
        var leftOut = new List<LeftOutEntry>();
        var scopes = new Dictionary<int, string?>();
        var repeatSlots = new Dictionary<TreeSlot, int>();
        var parsed = draft.Entries
            .Select((entry, index) => (Entry: entry, Index: index, Path: TemplatePath.TryParse(entry.Target, out var path, out _) ? path : null))
            .ToList();

        foreach (var (entry, index, path) in parsed.Where(p => p.Path is { IsRepeated: false }))
        {
            if (Place(root, path!.Segments.Select(s => s.Name).ToList(), entry, path.Text, leftOut) is { } slot && entry.Input == MappingDraftInput.Repeat)
            {
                repeatSlots[slot] = index;
            }
        }

        foreach (var (entry, index, path) in parsed.Where(p => p.Path is { IsRepeated: true }))
        {
            var array = path!.Repeater!;
            var holder = Find(root, array.Segments.Select(s => s.Name).ToList());
            if (holder is not { Entry.Input: MappingDraftInput.Repeat } repeat)
            {
                leftOut.Add(new LeftOutEntry(path.Text, holder is { Entry.Input: MappingDraftInput.List }
                    ? $"{path.Text} fills a property of the items of {array.Text}, whose items a list writes; fill it in an item of that list"
                    : $"{path.Text} fills a property of the items of {array.Text}, and no entry repeats a child dataset's rows at {array.Text}"));
                continue;
            }

            if (Place(repeat.Properties!, path.WithinItem, entry, path.Text, leftOut) is not null)
            {
                scopes[index] = string.IsNullOrWhiteSpace(repeat.Entry!.Child) ? null : repeat.Entry.Child.Trim();
            }
        }

        var empty = repeatSlots.Where(kv => kv.Key.Properties is { Count: 0 }).Select(kv => kv.Value).ToHashSet();
        return new DraftLayout(root, leftOut, scopes, empty);
    }

    /// <summary>Places one entry at <paramref name="names"/> below <paramref name="properties"/>, or says why it has no place there.</summary>
    private static TreeSlot? Place(List<(string Name, TreeSlot Slot)> properties, IReadOnlyList<string> names, MappingDraftEntry entry, string target, List<LeftOutEntry> leftOut)
    {
        var current = properties;
        for (var i = 0; i < names.Count - 1; i++)
        {
            var existing = current.FirstOrDefault(p => p.Name == names[i]).Slot;
            if (existing?.Entry is { } whole)
            {
                leftOut.Add(new LeftOutEntry(target, $"{target} lies inside {whole.Target.Trim()}, which an entry fills whole"));
                return null;
            }

            if (existing is null)
            {
                existing = new TreeSlot { Properties = [] };
                current.Add((names[i], existing));
            }

            current = existing.Properties!;
        }

        var leaf = current.FirstOrDefault(p => p.Name == names[^1]).Slot;
        if (leaf is not null)
        {
            leftOut.Add(leaf.Entry is null
                ? new LeftOutEntry(target, $"{target} is filled whole, and other entries fill properties inside it; fill it whole or fill its properties")
                : new LeftOutEntry(target, null));
            return null;
        }

        var slot = new TreeSlot { Entry = entry, Properties = entry.Input == MappingDraftInput.Repeat ? [] : null };
        current.Add((names[^1], slot));
        return slot;
    }

    private static TreeSlot? Find(List<(string Name, TreeSlot Slot)> properties, IReadOnlyList<string> names)
    {
        TreeSlot? slot = null;
        var current = properties;
        foreach (var name in names)
        {
            slot = current?.FirstOrDefault(p => p.Name == name).Slot;
            if (slot is null)
            {
                return null;
            }

            current = slot.Properties;
        }

        return slot;
    }

    /// <summary>Lines laid out at <paramref name="indent"/> + 2 written as one list item at <paramref name="indent"/>: the item's dash beside the first of them.</summary>
    private static void WriteDashed(IReadOnlyList<string> lines, int indent, Action<string> line)
    {
        var pad = new string(' ', indent);
        for (var i = 0; i < lines.Count; i++)
        {
            line(i == 0 ? pad + "- " + lines[i].TrimStart() : lines[i]);
        }
    }

    private const string Marker = MappingMapper.Marker;

    private const string DatasetReference = MappingMapper.DatasetReference;

    /// <summary>
    /// A draft column (<c>column</c> for the dataset's own row, <c>child.column</c> for a child dataset's) as the node that
    /// reads it in <paramref name="scope"/> writes it: inside the items of the array repeating <c>child</c>, its columns are
    /// named bare and the dataset's own row as <c>$dataset.column</c>. A child column read anywhere else is written as it
    /// stands, for the loader to report.
    /// </summary>
    private static string ScopedColumn(string column, string? scope)
    {
        var trimmed = column.Trim();
        var dot = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return scope is null || trimmed.Length == 0 ? trimmed : DatasetReference + "." + trimmed;
        }

        return string.Equals(trimmed[..dot], scope, StringComparison.Ordinal) ? trimmed[(dot + 1)..] : trimmed;
    }

    /// <summary>Text for a YAML comment, on one line.</summary>
    private static string Comment(string text) => text.ReplaceLineEndings(" ");

    /// <summary>A text value as a YAML scalar: plain where YAML reads it back as the same text, double-quoted otherwise.</summary>
    internal static string Scalar(string value) => Plain(value, BlockPlain()) ? value : Quote(value);

    /// <summary>A text value as a YAML scalar inside a flow collection, where brackets, braces and commas are not plain.</summary>
    internal static string FlowScalar(string value) => Plain(value, FlowPlain()) ? value : Quote(value);

    private static bool Plain(string value, Regex pattern)
        => value.Length > 0
           && pattern.IsMatch(value)
           && !ReservedWords.Contains(value)
           && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
           && !value.StartsWith('.');

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";

    [GeneratedRegex(@"^[A-Za-z0-9_$(][A-Za-z0-9_$.\/@%+()\[\]\-]*(?: [A-Za-z0-9_$.\/@%+()\[\]\-='=]+)*$")]
    private static partial Regex BlockPlain();

    [GeneratedRegex(@"^[A-Za-z0-9_$(][A-Za-z0-9_$.\/@%+()\-]*$")]
    private static partial Regex FlowPlain();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex ColumnName();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)?$")]
    private static partial Regex DatasetColumnPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\$]+(\.[A-Za-z0-9_\-\$]+)*$")]
    private static partial Regex FieldPath();
}
